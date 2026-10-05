using System.Net;
using Npgsql;

namespace Pulse.Api.Configuration;

/// <summary>
/// Development mode turns on things no shared server should have: Swagger, exception messages and stack traces in 500
/// responses, debug-level logging with every SQL statement, and (with ENABLE_DEMO_ENDPOINTS) the endpoints that wipe the
/// database. A server whose ASPNETCORE_ENVIRONMENT is wrongly left at Development looks exactly like a developer's machine
/// except for where its database lives, so a Development process pointed at a non-local database says so, loudly, at startup.
/// </summary>
public static class DevelopmentEnvironmentGuard
{
    /// <summary>A loopback address, or a bare name with no dots (a docker-compose service such as "postgres").</summary>
    public static bool IsLocalHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true; // no host given: the driver's own default, which is local
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (host.Equals("host.docker.internal", StringComparison.OrdinalIgnoreCase)) return true;
        if (IPAddress.TryParse(host, out var ip)) return IPAddress.IsLoopback(ip);
        return !host.Contains('.');
    }

    public static string? DatabaseHost(string connectionString)
    {
        try { return new NpgsqlConnectionStringBuilder(connectionString).Host; }
        catch (ArgumentException) { return null; }
    }

    public static void WarnIfNotLocal(WebApplication app, string connectionString)
    {
        if (!app.Environment.IsDevelopment()) return;

        var host = DatabaseHost(connectionString);
        if (IsLocalHost(host)) return;

        app.Logger.LogCritical(
            "ASPNETCORE_ENVIRONMENT is Development but the database host is {Host}, which is not local. In Development this " +
            "server exposes Swagger, returns exception messages and stack traces from failing requests, and logs every SQL " +
            "statement. Set ASPNETCORE_ENVIRONMENT=Production on any shared server, and make sure ENABLE_DEMO_ENDPOINTS is not set.",
            host);
    }
}
