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
        DevelopmentEnvironmentGuard.WarnIfNotLocal(app, appSettings.MigrationConnectionString);
        // The throwaway context has no HTTP request, so it must say who it is: as the service role, row-level
        // security lets a data migration see every row. Without it a data-changing migration is silently filtered
        // (touches zero rows yet is recorded as applied) whenever the migration role does not bypass RLS.
        var migrationOptions = new DbContextOptionsBuilder<PulseDbContext>()
            .UseNpgsql(appSettings.MigrationConnectionString)
            .AddInterceptors(new ServiceIdentityConnectionInterceptor())
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

        // Read-only startup checks below can use the runtime context.
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();

        // OverworkThresholds are loaded per organization on first use (OrganizationThresholdsProvider);
        // there's no process-wide copy to hydrate at startup any more.

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
