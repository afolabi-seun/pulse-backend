using System.Text;
using System.Threading.RateLimiting;
using Asp.Versioning;
using Pulse.Api.Hubs;
using Microsoft.AspNetCore.Mvc;
using Pulse.Api.Security;
using Pulse.Application.Alerts;
using Pulse.Application.Automations;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.CheckIns;
using Pulse.Application.Escalations;
using Pulse.Application.Estimation;
using Pulse.Application.Overwork;
using Pulse.Application.Vitals;
using Pulse.Application.TimeEntries;
using Pulse.Infrastructure.BackgroundJobs;
using Pulse.Infrastructure.DemoData;
using Pulse.Infrastructure.Email;
using Pulse.Infrastructure.Persistence;
using Pulse.Infrastructure.AlertExplanations;
using Pulse.Infrastructure.Persistence.Repositories;
using Pulse.Infrastructure.Security;
using Pulse.Infrastructure.GoogleChat;
using Pulse.Infrastructure.Slack;
using Pulse.Infrastructure.Webhooks;
using FluentValidation;
using FluentValidation.AspNetCore;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace Pulse.Api.Configuration;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPulseInfrastructure(this IServiceCollection services, AppSettings settings)
    {
        // RLS identity plumbing: the interceptor stamps app.current_role / app.current_user_id
        // onto every connection so Postgres row-level security policies can see the caller.
        services.AddHttpContextAccessor();
        services.AddScoped<IRlsContext, HttpRlsContext>();
        services.AddScoped<RlsConnectionInterceptor>();

        services.AddDbContext<PulseDbContext>((sp, opts) =>
            opts.UseNpgsql(settings.DatabaseConnectionString)
                .AddInterceptors(sp.GetRequiredService<RlsConnectionInterceptor>()));

        services.AddScoped<IPasswordHasher, Argon2PasswordHasher>();
        services.AddScoped<IJwtService, JwtService>();
        services.AddScoped<MailKitEmailService>();
        services.AddScoped<IEmailService>(sp => sp.GetRequiredService<MailKitEmailService>());
        services.AddScoped<IDirectEmailSender>(sp => sp.GetRequiredService<MailKitEmailService>());
        services.AddScoped<IAuditLogRepository, AuditLogRepository>();
        services.AddScoped<IEngineerRepository, EngineerRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IProjectRepository, ProjectRepository>();
        services.AddScoped<ITaskRepository, TaskRepository>();
        services.AddScoped<ICheckInRepository, CheckInRepository>();
        services.AddScoped<ITimeEntryRepository, TimeEntryRepository>();
        services.AddScoped<IActiveTimerRepository, ActiveTimerRepository>();
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<IEscalationEventRepository, EscalationEventRepository>();
        services.AddScoped<IOverworkOverrideRepository, OverworkOverrideRepository>();
        services.AddScoped<IFeedbackRepository, FeedbackRepository>();
        services.AddScoped<IVitalsRepository, VitalsRepository>();
        services.AddScoped<IThresholdRepository, ThresholdRepository>();
        services.AddScoped<IDepartmentThresholdRepository, DepartmentThresholdRepository>();
        services.AddScoped<ITeamRepository, TeamRepository>();
        services.AddScoped<ISprintRepository, SprintRepository>();
        services.AddScoped<IEpicRepository, EpicRepository>();
        services.AddScoped<ITaskDependencyRepository, TaskDependencyRepository>();
        services.AddScoped<ISubtaskRepository, SubtaskRepository>();
        services.AddScoped<IEstimationRepository, EstimationRepository>();
        services.AddScoped<ITaskCommentRepository, TaskCommentRepository>();
        services.AddScoped<IRetroRepository, RetroRepository>();
        services.AddScoped<IProjectFollowRepository, ProjectFollowRepository>();
        services.AddScoped<IWikiRepository, WikiPageRepository>();
        services.AddScoped<IWeeklyReportRepository, WeeklyReportRepository>();
        services.AddScoped<IRealtimeNotifier, SignalRNotifier>();
        services.AddScoped<IProjectAccessPolicy, ProjectAccessPolicy>();
        services.AddScoped<IFailedEmailRepository, FailedEmailRepository>();
        services.AddScoped<IEmailQueue, HangfireEmailQueue>();
        services.AddScoped<IWebhookNotifier, HangfireWebhookNotifier>();

        services.AddHttpClient<IBreachedPasswordChecker, HibpBreachedPasswordChecker>(client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Pulse/1.0");
        });

        services.AddHttpClient<IWebhookSender, WebhookSender>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        services.AddHttpClient<IAlertExplainer, AnthropicAlertExplainer>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddHttpClient<ISlackClient, SlackClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        services.AddHttpClient<IGoogleChatMessenger, GoogleChatMessenger>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddScoped<IGoogleChatRequestVerifier, GoogleChatRequestVerifier>();
        services.AddScoped<IGoogleChatSpaceRepository, GoogleChatSpaceRepository>();
        services.AddScoped<IGoogleChatThreadRepository, GoogleChatThreadRepository>();
        services.AddScoped<IAutomationRuleRepository, AutomationRuleRepository>();
        services.AddScoped<IAutomationExecutionRepository, AutomationExecutionRepository>();

        services.AddScoped<IDemoSeeder, DemoSeeder>();

        return services;
    }

    public static IServiceCollection AddPulseAuth(this IServiceCollection services, AppSettings settings)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(opts =>
            {
                opts.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.JwtSecretKey)),
                    ValidateIssuer = true,
                    ValidIssuer = settings.JwtIssuer,
                    ValidateAudience = true,
                    ValidAudience = settings.JwtAudience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30)
                };

                // SignalR WebSocket handshake cannot carry the Authorization header in browsers,
                // so the client sends the token as ?access_token=... on the /hubs path.
                opts.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ctx =>
                    {
                        var token = ctx.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(token) &&
                            ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                            ctx.Token = token;
                        return Task.CompletedTask;
                    }
                };
            });

        services.AddAuthorization();

        return services;
    }

    public static IServiceCollection AddPulseApiVersioning(this IServiceCollection services)
    {
        services.AddApiVersioning(opts =>
        {
            opts.DefaultApiVersion = new ApiVersion(1, 0);
            opts.AssumeDefaultVersionWhenUnspecified = true;
            opts.ReportApiVersions = true;
        }).AddMvc();

        return services;
    }

    public static IServiceCollection AddPulseHangfire(this IServiceCollection services, AppSettings settings)
    {
        services.AddHangfire(cfg => cfg
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            // Hangfire owns and creates its own schema — use the privileged (owner) connection.
            .UsePostgreSqlStorage(opts => opts.UseNpgsqlConnection(settings.MigrationConnectionString)));

        services.AddHangfireServer();

        services.AddScoped<EscalationScanner>();
        services.AddScoped<EstimateApprovalEscalationScanner>();
        services.AddScoped<IAlertRuleRepository, AlertRuleRepository>();
        services.AddScoped<IAlertMetricsProvider, AlertMetricsProvider>();
        services.AddScoped<IAlertConversationRepository, AlertConversationRepository>();
        services.AddScoped<IAlertMetricSnapshotRepository, AlertMetricSnapshotRepository>();
        services.AddScoped<AlertRuleScanner>();
        services.AddScoped<AutomationRuleScanner>();
        services.AddScoped<CheckInReminderJob>();
        services.AddScoped<TimeEntryReminderJob>();
        services.AddScoped<CheckInNudgeJob>();
        services.AddScoped<WeeklyVitalsPromptJob>();
        services.AddScoped<SendEmailJob>();
        services.AddScoped<OverworkDigestJob>();
        services.AddSingleton(new OverworkThresholds());
        services.AddSingleton<OverworkSignalsCalculator>();
        services.AddScoped<EngineerWorkloadAssessor>();

        return services;
    }

    public static IServiceCollection AddPulseSignalR(this IServiceCollection services)
    {
        services.AddSignalR();
        services.AddSingleton<Microsoft.AspNetCore.SignalR.IUserIdProvider, PulseUserIdProvider>();
        return services;
    }

    public static IServiceCollection AddPulseMediatR(this IServiceCollection services)
    {
        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssemblyContaining<ServiceResult<object>>());

        return services;
    }

    public static IServiceCollection AddPulseValidation(this IServiceCollection services)
    {
        services.AddFluentValidationAutoValidation();
        services.AddValidatorsFromAssemblyContaining<ServiceResult<object>>();
        services.AddValidatorsFromAssemblyContaining<Program>();

        // Reformat FluentValidation 400 responses to match the Pulse ApiResponse envelope.
        // Without this, ASP.NET emits a raw ValidationProblemDetails that the frontend
        // interceptor can't parse — it falls back to "A network error occurred."
        services.Configure<ApiBehaviorOptions>(options =>
        {
            options.InvalidModelStateResponseFactory = ctx =>
            {
                // ModelState keys are usually property/parameter names ("Title"), but implicit-required
                // validation on a missing top-level parameter (e.g. an absent IFormFile) reports against
                // the empty-string key — camelCasing that would index past an empty string.
                var errors = ctx.ModelState
                    .Where(kv => kv.Value?.Errors.Count > 0)
                    .ToDictionary(
                        kv => kv.Key.Length > 0 ? char.ToLowerInvariant(kv.Key[0]) + kv.Key[1..] : kv.Key,
                        kv => kv.Value!.Errors.Select(x => x.ErrorMessage).ToArray());

                return new BadRequestObjectResult(new ApiResponse<object>
                {
                    Status = "error",
                    Error  = new ApiError("VALIDATION_ERROR", "One or more validation errors occurred.")
                    {
                        Errors = errors
                    }
                });
            };
        });

        return services;
    }

    public static IServiceCollection AddPulseSwagger(this IServiceCollection services)
    {
        services.AddControllers();
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(opts =>
        {
            opts.SwaggerDoc("v1", new() { Title = "Pulse API", Version = "v1" });

            var xmlFile = $"{typeof(Program).Assembly.GetName().Name}.xml";
            var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
            if (File.Exists(xmlPath))
                opts.IncludeXmlComments(xmlPath);

            opts.AddSecurityDefinition("Bearer", new()
            {
                Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT"
            });
            opts.AddSecurityRequirement(new()
            {
                {
                    new() { Reference = new() { Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "Bearer" } },
                    []
                }
            });
        });

        return services;
    }

    public static IServiceCollection AddPulseHealthChecks(this IServiceCollection services, AppSettings settings)
    {
        services.AddHealthChecks()
            .AddNpgSql(settings.DatabaseConnectionString, name: "postgres", tags: ["ready"]);

        return services;
    }

    public static IServiceCollection AddPulseForwardedHeaders(this IServiceCollection services)
    {
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            // ForwardLimit defaults to 1 — only one hop of X-Forwarded-For gets unwound. If anything
            // sits in front of nginx itself (a platform load balancer, another reverse proxy, etc.),
            // that default stops one hop too early: it correctly trusts nginx and peels its entry, but
            // then halts on the *next* hop's own address instead of continuing to the real client. Null
            // means "keep peeling as long as the current address is still in the trusted list below" —
            // it self-terminates at the first address that isn't, which is what makes this safe: a
            // genuine external client can never present as one of these non-routable ranges, so the
            // loop can only ever stop for real at the actual client, however many trusted hops precede it.
            options.ForwardLimit = null;
            // Trust only the immediate upstream proxy (nginx). Prevents IP spoofing via a crafted
            // X-Forwarded-For header from the public internet — but that trust must actually cover
            // wherever nginx's connection to this process appears to originate from, or the whole
            // pipeline silently falls back to nginx's own address for every request (breaking rate
            // limiting and audit-log IPs alike, since both read Connection.RemoteIpAddress).
            options.KnownNetworks.Clear();
            options.KnownProxies.Clear();
            // nginx as a separate container reached over a Docker bridge network:
            options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(System.Net.IPAddress.Parse("10.0.0.0"), 8));
            options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(System.Net.IPAddress.Parse("172.16.0.0"), 12));
            options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(System.Net.IPAddress.Parse("192.168.0.0"), 16));
            // nginx on the same host, proxying over loopback: a real external client's TCP connection
            // can never genuinely present as 127.0.0.1/::1, so trusting it as a forwarding hop is safe
            // regardless of which of these two topologies is actually in front of this process.
            options.KnownProxies.Add(System.Net.IPAddress.Loopback);
            options.KnownProxies.Add(System.Net.IPAddress.IPv6Loopback);
        });

        return services;
    }

    public static IServiceCollection AddPulseCors(this IServiceCollection services, AppSettings settings)
    {
        services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                policy.WithOrigins(settings.AllowedOrigins)
                      .AllowAnyHeader()
                      .AllowAnyMethod()
                      .AllowCredentials(); // required for SignalR WebSocket and cookie-less JWT flows
            });
        });

        return services;
    }

    public static IServiceCollection AddPulseRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, ct) =>
            {
                var message = "Too many requests. Please try again later.";
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
                    context.HttpContext.Response.Headers.RetryAfter = seconds.ToString();
                    message = $"Too many requests. Please try again in {seconds} second{(seconds == 1 ? "" : "s")}.";
                }

                context.HttpContext.Response.ContentType = "application/json";
                await context.HttpContext.Response.WriteAsJsonAsync(
                    ApiResponse<object>.Failure("RATE_LIMITED", message),
                    ct);
            };

            // See PulseRateLimits: login and password reset by IP; every signed-in user by id; and the few
            // writes that notify other people by id, more tightly. No named policies, so tests can null this out.
            options.GlobalLimiter = PulseRateLimits.CreateGlobalLimiter();
        });

        return services;
    }
}
