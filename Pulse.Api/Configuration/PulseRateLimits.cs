using System.Security.Claims;
using System.Threading.RateLimiting;

namespace Pulse.Api.Configuration;

/// <summary>
/// The API's request limits, in one place. Three independent limits are chained, and a request must pass all of them:
/// <list type="bullet">
/// <item><b>By IP, for the unauthenticated entry points</b> — login and password reset (each hit sends an email).</item>
/// <item><b>By signed-in user, for everything</b> — a ceiling no ordinary use gets near, so a script or a stuck client
/// can't hammer any endpoint. Keyed on the user, not the IP, because an office shares one address.</item>
/// <item><b>By signed-in user, for the few writes that notify other people</b> (comments, feedback, estimates sent for
/// approval) — each one can email or notify someone, so they get a much tighter ceiling.</item>
/// </list>
/// The limiter must run after authentication, or there is no user to key on.
/// </summary>
public static class PulseRateLimits
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>Requests a signed-in user may make per minute — ten a second, held for a minute. A page load is a few dozen
    /// calls and the app caches most of them, so a person moving quickly stays well under; a script does not.</summary>
    public const int PerUserPerMinute = 600;

    /// <summary>Notifying writes a signed-in user may make per minute.</summary>
    public const int NotifyingPerUserPerMinute = 30;

    public const int LoginPerIpPerMinute = 10;
    public const int PasswordResetPerIpPer5Minutes = 5;

    /// <summary>Paths that are not API calls: the realtime connection, and the health checks the platform polls.</summary>
    private static readonly string[] ExemptPrefixes = ["/hubs", "/healthz", "/health", "/hangfire"];

    public static PartitionedRateLimiter<HttpContext> CreateGlobalLimiter() =>
        PartitionedRateLimiter.CreateChained(
            PartitionedRateLimiter.Create<HttpContext, string>(ByIp),
            PartitionedRateLimiter.Create<HttpContext, string>(ByUser),
            PartitionedRateLimiter.Create<HttpContext, string>(NotifyingByUser));

    public static RateLimitPartition<string> ByIp(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        if (path.Contains("/auth/login", StringComparison.OrdinalIgnoreCase))
            return RateLimitPartition.GetFixedWindowLimiter($"login:{ip}",
                _ => new FixedWindowRateLimiterOptions { Window = Window, PermitLimit = LoginPerIpPerMinute, QueueLimit = 0 });

        if (path.Contains("/auth/password-reset", StringComparison.OrdinalIgnoreCase) &&
            !path.Contains("/confirm", StringComparison.OrdinalIgnoreCase))
            return RateLimitPartition.GetFixedWindowLimiter($"pwreset:{ip}",
                _ => new FixedWindowRateLimiterOptions { Window = TimeSpan.FromMinutes(5), PermitLimit = PasswordResetPerIpPer5Minutes, QueueLimit = 0 });

        return RateLimitPartition.GetNoLimiter("ip:none");
    }

    public static RateLimitPartition<string> ByUser(HttpContext ctx)
    {
        var userId = UserId(ctx);
        if (userId is null || IsExempt(ctx))
            return RateLimitPartition.GetNoLimiter("user:none");

        return RateLimitPartition.GetFixedWindowLimiter($"user:{userId}",
            _ => new FixedWindowRateLimiterOptions { Window = Window, PermitLimit = PerUserPerMinute, QueueLimit = 0 });
    }

    public static RateLimitPartition<string> NotifyingByUser(HttpContext ctx)
    {
        var userId = UserId(ctx);
        if (userId is null || !IsNotifyingWrite(ctx))
            return RateLimitPartition.GetNoLimiter("notifying:none");

        return RateLimitPartition.GetFixedWindowLimiter($"notifying:{userId}",
            _ => new FixedWindowRateLimiterOptions { Window = Window, PermitLimit = NotifyingPerUserPerMinute, QueueLimit = 0 });
    }

    /// <summary>A write that tells someone else about it: a comment (mentions), feedback, or an estimate sent for approval.</summary>
    public static bool IsNotifyingWrite(HttpContext ctx)
    {
        if (!HttpMethods.IsPost(ctx.Request.Method)) return false;
        var path = (ctx.Request.Path.Value ?? "").TrimEnd('/');
        return path.EndsWith("/comments", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/feedback", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/submit-for-approval", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExempt(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        return ExemptPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    private static string? UserId(HttpContext ctx) =>
        ctx.User.Identity?.IsAuthenticated == true ? ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) : null;
}
