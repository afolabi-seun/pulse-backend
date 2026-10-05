using Pulse.Application.Common.Interfaces;
using Pulse.Application.Notifications;
using Pulse.Application.Tasks;
using Pulse.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Pulse.IntegrationTests.Infrastructure;

/// <summary>
/// Spins up the full ASP.NET Core pipeline against a real PostgreSQL database. All test classes share one Testcontainers
/// Postgres (see SharedPostgres); each class gets its own database cloned from a template that already has every migration applied.
/// The HIBP checker is replaced with a no-op stub so tests never make external HTTP calls.
/// </summary>
public class PulseWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private string _connectionString = string.Empty;

    /// <summary>Raw connection string for tests that bypass EF to verify row-level security at the DB layer. Points at this class's own database.</summary>
    public string ConnectionString => _connectionString;

    public async Task InitializeAsync()
    {
        _connectionString = await SharedPostgres.CreateDatabaseAsync();

        // AppSettings.FromEnvironment() is called in Program.cs before the factory can
        // override services. Set the minimum required env vars here so it doesn't throw.
        // The DB context is replaced below with the Testcontainers connection.
        Environment.SetEnvironmentVariable("DB_CONNECTION", _connectionString);
        Environment.SetEnvironmentVariable("JWT_SECRET_KEY", "test-signing-key-minimum-32-chars-long!!");
        Environment.SetEnvironmentVariable("JWT_ISSUER", "pulse");
        Environment.SetEnvironmentVariable("JWT_AUDIENCE", "pulse");
        Environment.SetEnvironmentVariable("JWT_ACCESS_EXPIRY_TIME", "30");
        Environment.SetEnvironmentVariable("JWT_VERIFY_EMAIL_EXPIRY_TIME", "24");
        Environment.SetEnvironmentVariable("JWT_FORGET_PASSWORD_EXPIRY_TIME", "15");
        Environment.SetEnvironmentVariable("APP_BASE_URL", "http://localhost:5173");
        Environment.SetEnvironmentVariable("ALLOWED_ORIGINS", "http://localhost:5173");
        Environment.SetEnvironmentVariable("Mail_Host", "localhost");
        Environment.SetEnvironmentVariable("Mail_Port", "25");
        Environment.SetEnvironmentVariable("Mail_User", "test");
        Environment.SetEnvironmentVariable("Mail_Password", "test");
        Environment.SetEnvironmentVariable("Mail_FromName", "Pulse Test");
        Environment.SetEnvironmentVariable("Mail_FromAddress", "test@pulse.test");
        Environment.SetEnvironmentVariable("Mail_EnableSsl", "false");
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();                                   // close the host's connections first
        await SharedPostgres.DropDatabaseAsync(_connectionString);   // the container itself outlives the class and is reaped with the run
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // The wipe-everything demo endpoints are opt-in on top of the Development environment (see DemoController).
        builder.UseSetting("ENABLE_DEMO_ENDPOINTS", "true");

        builder.ConfigureServices(services =>
        {
            // Replace real DB context with Testcontainers connection
            var descriptor = services.Single(d => d.ServiceType == typeof(DbContextOptions<PulseDbContext>));
            services.Remove(descriptor);
            var connStr = _connectionString + ";Include Error Detail=true";
            // Keep the RLS interceptor attached — without it no session GUCs are set and the
            // FORCEd row-level policies would filter out every row, breaking the whole suite.
            services.AddDbContext<PulseDbContext>((sp, opts) =>
                opts.UseNpgsql(connStr)
                    .AddInterceptors(sp.GetRequiredService<RlsConnectionInterceptor>()));

            // Replace HIBP checker with a stub that never flags breached passwords in tests
            services.RemoveAll<IBreachedPasswordChecker>();
            services.AddSingleton<IBreachedPasswordChecker, AlwaysCleanPasswordChecker>();

            // Argon2id (64 MB, 4 passes) is deliberately slow, and these tests seed several users and log in several times each, so it was
            // a large share of every test. Hashing is not what they exercise (Pulse.UnitTests.Security.Argon2PasswordHasherTests covers the real one), so they use a
            // fast hasher with the same salted "salt:hash" format. Everything resolves IPasswordHasher from DI, so seeding and login agree.
            services.RemoveAll<IPasswordHasher>();
            services.AddSingleton<IPasswordHasher, FastTestPasswordHasher>();

            // Replace email service with a no-op stub so tests don't attempt SMTP connections
            services.RemoveAll<IEmailService>();
            services.AddSingleton<IEmailService, NoOpEmailService>();

            // Replace SignalR notifier with a no-op stub — no WebSocket server in tests
            services.RemoveAll<IRealtimeNotifier>();
            services.AddSingleton<IRealtimeNotifier, NoOpRealtimeNotifier>();

            // Disable the global rate limiter so repeated logins in tests aren't throttled
            services.Configure<RateLimiterOptions>(options =>
            {
                options.GlobalLimiter = null;
            });

            // Apply migrations before the first test runs
            var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            db.Database.Migrate();
        });
    }

    /// <summary>Stub HIBP checker — returns false so tests can use any password without making HTTP calls.</summary>
    private sealed class AlwaysCleanPasswordChecker : IBreachedPasswordChecker
    {
        public Task<bool> IsBreachedAsync(string password, CancellationToken ct = default) =>
            Task.FromResult(false);
    }

    /// <summary>Same shape as the real hasher (a random salt, then the hash) but a single SHA-256, so tests are not paying Argon2's cost.</summary>
    private sealed class FastTestPasswordHasher : IPasswordHasher
    {
        public string Hash(string password)
        {
            var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
            return $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(Compute(password, salt))}";
        }

        public bool Verify(string password, string storedHash)
        {
            var parts = storedHash.Split(':');
            if (parts.Length != 2) return false;
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                Compute(password, Convert.FromBase64String(parts[0])), Convert.FromBase64String(parts[1]));
        }

        private static byte[] Compute(string password, byte[] salt) =>
            System.Security.Cryptography.SHA256.HashData([.. salt, .. System.Text.Encoding.UTF8.GetBytes(password)]);
    }

    /// <summary>Stub email service — silently discards all outgoing mail in tests.</summary>
    public sealed class NoOpEmailService : IEmailService
    {
        public Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    /// <summary>Stub realtime notifier — no-op in tests where no SignalR clients are connected.</summary>
    private sealed class NoOpRealtimeNotifier : IRealtimeNotifier
    {
        public Task SendNotificationAsync(Guid userId, NotificationDto notification, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task SendTaskUpdatedAsync(Guid assigneeId, TaskDto task, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task SendRoleChangedAsync(Guid userId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
