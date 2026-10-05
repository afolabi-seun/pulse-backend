using System.Text.Json;
using Pulse.Api.Hubs;
using Pulse.Api.Middleware;
using Pulse.Application.Overwork;
using Pulse.Domain.Engineers;
using Pulse.Infrastructure.BackgroundJobs;
using Pulse.Infrastructure.Persistence;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace Pulse.Api.Configuration;

public static class ApplicationBuilderExtensions
{
    public static async Task ApplyMigrationsAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();

        // Migrations need the privileged (owner) connection — the runtime DbContext connects as the
        // limited, RLS-bound role which lacks DDL rights. Build a throwaway context for the migration.
        var appSettings = scope.ServiceProvider.GetRequiredService<Application.Common.Interfaces.IAppSettings>();
        // The throwaway context has no HTTP request, so it must say who it is: as the service role, row-level
        // security lets a data migration see every row. Without it a data-changing migration is silently filtered
        // (touches zero rows yet is recorded as applied) whenever the migration role does not bypass RLS.
        var migrationOptions = new DbContextOptionsBuilder<PulseDbContext>()
            .UseNpgsql(appSettings.MigrationConnectionString)
            .AddInterceptors(new RlsConnectionInterceptor(new ServiceRlsContext()))
            .Options;
        try
        {
            await using var migrationDb = new PulseDbContext(migrationOptions);
            await migrationDb.Database.MigrateAsync();
        }
        catch (Exception ex)
        {
            // Fail fast — never serve against a half-migrated DB — but make the cause obvious in logs.
            app.Logger.LogCritical(ex,
                "Startup database migration failed. Common causes: DB unreachable, or DB_CONNECTION points at the " +
                "limited RLS runtime role without DB_MIGRATION_CONNECTION set to an owner role that has DDL rights " +
                "(see docs/rls-runbook.md).");
            throw;
        }

        // Read-only hydration below can use the runtime context (threshold_settings is not RLS-scoped).
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();

        // Hydrate the OverworkThresholds singleton from DB so custom values survive restarts.
        // Non-critical: defaults exist, so a failure here must not prevent the API from booting.
        try
        {
        var settings = await db.ThresholdSettings.ToDictionaryAsync(t => t.Key, t => t.Value);
        var thresholds = app.Services.GetRequiredService<OverworkThresholds>();

        if (settings.TryGetValue("LoadVsBaselineRatio", out var v1) && double.TryParse(v1, out var ratio))
            thresholds.LoadVsBaselineRatio = ratio;
        if (settings.TryGetValue("MaxConcurrentTasks", out var v2) && int.TryParse(v2, out var concurrent))
            thresholds.MaxConcurrentTasks = concurrent;
        if (settings.TryGetValue("StaleCycleMultiplier", out var v3) && double.TryParse(v3, out var stale))
            thresholds.StaleCycleMultiplier = stale;
        if (settings.TryGetValue("SignalsRequiredToFlag", out var v4) && int.TryParse(v4, out var signals))
            thresholds.SignalsRequiredToFlag = signals;
        if (settings.TryGetValue("EscalationT3Days", out var v5) && double.TryParse(v5, out var t3Days))
            thresholds.EscalationT3Days = t3Days;
        if (settings.TryGetValue("EscalationT3ElapsedPct", out var v6) && double.TryParse(v6, out var t3Pct))
            thresholds.EscalationT3ElapsedPct = t3Pct;
        if (settings.TryGetValue("EscalationT1Days", out var v7) && double.TryParse(v7, out var t1Days))
            thresholds.EscalationT1Days = t1Days;
        if (settings.TryGetValue("EscalationT1ElapsedPct", out var v8) && double.TryParse(v8, out var t1Pct))
            thresholds.EscalationT1ElapsedPct = t1Pct;
        if (settings.TryGetValue("EscalationT3MinHours", out var v9) && double.TryParse(v9, out var t3MinH))
            thresholds.EscalationT3MinHours = t3MinH;
        if (settings.TryGetValue("EscalationT1MinHours", out var v10) && double.TryParse(v10, out var t1MinH))
            thresholds.EscalationT1MinHours = t1MinH;
        if (settings.TryGetValue("QaLeadTimeDays", out var v11) && int.TryParse(v11, out var qaLead))
            thresholds.QaLeadTimeDays = qaLead;
        if (settings.TryGetValue("PointScale", out var psJson) && !string.IsNullOrEmpty(psJson))
        {
            var entries = JsonSerializer.Deserialize<PointScaleEntry[]>(psJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (entries is { Length: > 0 })
                thresholds.PointScale = entries;
        }
        if (settings.TryGetValue("PriorityScale", out var prJson) && !string.IsNullOrEmpty(prJson))
        {
            var entries = JsonSerializer.Deserialize<PriorityScaleEntry[]>(prJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (entries is { Length: > 0 })
                thresholds.PriorityScale = entries;
        }
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "Failed to hydrate overwork thresholds from the database; using defaults.");
        }

        // V1 assumption: exactly one project manager exists. Notification resolution targets a single PM.
        // This assertion is informational — it logs a warning but does not prevent startup.
        var pmCount = await db.Engineers.CountAsync(e => e.Role == Roles.ProjectManager && e.IsActive);
        if (pmCount > 1)
        {
            var logger = app.Services.GetRequiredService<ILogger<WebApplication>>();
            logger.LogWarning(
                "V1 assumption violated: {Count} active project managers found. " +
                "Notification fan-out will reach all of them. Remove this warning when multi-PM is fully validated.",
                pmCount);
        }
    }

    public static void RegisterHangfireJobs(this WebApplication app)
    {
        var manager = app.Services.GetRequiredService<IRecurringJobManager>();
        HangfireJobRegistrar.RegisterRecurringJobs(manager);
    }

    public static void UsePulsePipeline(this WebApplication app)
    {
        // Kestrel's dual-stack socket represents every IPv4 connection (including nginx's) as an
        // IPv4-mapped IPv6 address (e.g. ::ffff:10.0.0.5). IPNetwork.Contains — which
        // UseForwardedHeaders relies on to decide whether the immediate hop is a trusted proxy —
        // does not unmap that form before comparing, so it never matches KnownNetworks/KnownProxies
        // even when the plain IPv4 address plainly would. Left unfixed, the proxy hop is never
        // trusted, X-Forwarded-For is never processed, and every request's RemoteIpAddress silently
        // stays nginx's own address instead of the real client's — breaking rate limiting (all
        // customers share nginx's bucket) and audit-log IPs alike. Must run before UseForwardedHeaders.
        app.Use(async (ctx, next) =>
        {
            if (ctx.Connection.RemoteIpAddress is { IsIPv4MappedToIPv6: true } mapped)
                ctx.Connection.RemoteIpAddress = mapped.MapToIPv4();
            await next();
        });

        // Must be first (after the normalization above) so RemoteIpAddress reflects the real client
        // IP before rate limiting and audit logging.
        app.UseForwardedHeaders();

        // Structured per-request log lines (method, path, status, elapsed). Placed after
        // UseForwardedHeaders so the logged IP is the real client IP, not the proxy.
        app.UseSerilogRequestLogging();

        app.UseMiddleware<GlobalExceptionHandlerMiddleware>();

        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers["X-Frame-Options"] = "DENY";
            ctx.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            await next();
        });

        app.UseCors();

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseAuthentication();
        // After authentication: the per-user limits are keyed on who is signed in. (The login and password-reset
        // limits are by IP and don't care where this sits.)
        app.UseRateLimiter();
        // RLS session settings are applied per-connection by RlsConnectionInterceptor (see AddPulseInfrastructure),
        // not by middleware — the old transaction-scoped middleware approach didn't survive to the data queries.
        app.UseAuthorization();
    }

    public static void MapPulseEndpoints(this WebApplication app)
    {
        app.MapControllers();
        app.MapHub<PulseHub>("/hubs/pulse");

        // Liveness — process is alive, no dependency checks. Docker HEALTHCHECK uses this.
        app.MapHealthChecks("/healthz", new HealthCheckOptions { Predicate = _ => false });

        // Readiness — all dependencies (Postgres) must be healthy before serving traffic.
        app.MapHealthChecks("/readyz", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready")
        });
        // Authorization = [] disables Hangfire's default LocalRequestsOnly filter;
        // RequireAuthorization enforces ASP.NET Core auth instead.
        app.MapHangfireDashboard("/hangfire", new DashboardOptions
        {
            IsReadOnlyFunc  = _ => true,
            Authorization   = []
        })
        .RequireAuthorization(new AuthorizeAttribute { Roles = Roles.HeadOfRnD });
    }
}
