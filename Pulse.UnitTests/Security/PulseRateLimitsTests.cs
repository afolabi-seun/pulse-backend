using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Pulse.Api.Configuration;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Pulse.UnitTests.Security;

public class PulseRateLimitsTests
{
    private static HttpContext Request(string method, string path, string? userId = null, string ip = "10.0.0.1")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        if (userId is not null)
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"));
        return ctx;
    }

    /// <summary>How many of the next <paramref name="attempts"/> requests the limiter lets through.</summary>
    private static int Allowed(PartitionedRateLimiter<HttpContext> limiter, Func<HttpContext> request, int attempts)
    {
        var allowed = 0;
        for (var i = 0; i < attempts; i++)
        {
            using var lease = limiter.AttemptAcquire(request());
            if (lease.IsAcquired) allowed++;
        }
        return allowed;
    }

    [Fact]
    public void A_signed_in_user_is_held_to_the_per_user_ceiling()
    {
        using var limiter = PulseRateLimits.CreateGlobalLimiter();

        Allowed(limiter, () => Request("GET", "/api/v1/tasks", "user-a"), PulseRateLimits.PerUserPerMinute + 50)
            .Should().Be(PulseRateLimits.PerUserPerMinute);
    }

    [Fact]
    public void One_users_traffic_does_not_use_up_another_users_allowance_even_from_the_same_ip()
    {
        using var limiter = PulseRateLimits.CreateGlobalLimiter();
        Allowed(limiter, () => Request("GET", "/api/v1/tasks", "noisy"), PulseRateLimits.PerUserPerMinute + 10);

        Allowed(limiter, () => Request("GET", "/api/v1/tasks", "quiet"), 20).Should().Be(20, "an office shares one IP");
    }

    [Fact]
    public void Ordinary_use_never_gets_near_the_ceiling()
    {
        using var limiter = PulseRateLimits.CreateGlobalLimiter();

        // A dashboard load is a few dozen calls; a dozen of them in a minute is already extreme.
        Allowed(limiter, () => Request("GET", "/api/v1/tasks", "user-a"), 12 * 40).Should().Be(12 * 40);
    }

    [Theory]
    [InlineData("POST", "/api/v1/tasks/123/comments")]
    [InlineData("POST", "/api/v1/feedback")]
    [InlineData("POST", "/api/v1/tasks/123/estimation/submit-for-approval")]
    public void Writes_that_notify_other_people_get_a_much_tighter_ceiling(string method, string path)
    {
        using var limiter = PulseRateLimits.CreateGlobalLimiter();

        Allowed(limiter, () => Request(method, path, "user-a"), 100).Should().Be(PulseRateLimits.NotifyingPerUserPerMinute);
        // ...and those 30 are all that the user's general allowance has used, so everything else still works.
        Allowed(limiter, () => Request("GET", "/api/v1/tasks", "user-a"), 50).Should().Be(50);
    }

    [Theory]
    [InlineData("GET", "/api/v1/tasks/123/comments")]   // reading comments notifies nobody
    [InlineData("GET", "/api/v1/feedback")]
    [InlineData("PUT", "/api/v1/tasks/123/comments")]
    public void Reads_and_unrelated_writes_are_not_held_to_the_notifying_ceiling(string method, string path)
    {
        using var limiter = PulseRateLimits.CreateGlobalLimiter();

        Allowed(limiter, () => Request(method, path, "user-a"), PulseRateLimits.NotifyingPerUserPerMinute + 20)
            .Should().Be(PulseRateLimits.NotifyingPerUserPerMinute + 20);
    }

    [Theory]
    [InlineData("/hubs/pulse")]
    [InlineData("/healthz")]
    public void Realtime_and_health_checks_are_not_limited(string path)
    {
        using var limiter = PulseRateLimits.CreateGlobalLimiter();

        Allowed(limiter, () => Request("GET", path, "user-a"), PulseRateLimits.PerUserPerMinute + 50)
            .Should().Be(PulseRateLimits.PerUserPerMinute + 50);
    }

    [Fact]
    public void Login_is_still_held_to_its_per_ip_limit()
    {
        using var limiter = PulseRateLimits.CreateGlobalLimiter();

        Allowed(limiter, () => Request("POST", "/api/v1/auth/login"), 30).Should().Be(PulseRateLimits.LoginPerIpPerMinute);
        Allowed(limiter, () => Request("POST", "/api/v1/auth/login", ip: "10.0.0.2"), 5).Should().Be(5, "another address has its own allowance");
    }

    [Fact]
    public void Password_reset_requests_are_still_held_to_their_per_ip_limit_but_confirming_is_not()
    {
        using var limiter = PulseRateLimits.CreateGlobalLimiter();

        Allowed(limiter, () => Request("POST", "/api/v1/auth/password-reset"), 20).Should().Be(PulseRateLimits.PasswordResetPerIpPer5Minutes);
        Allowed(limiter, () => Request("POST", "/api/v1/auth/password-reset/confirm"), 20).Should().Be(20);
    }

    [Fact]
    public void An_anonymous_request_to_an_ordinary_path_is_not_limited_by_the_per_user_rule()
    {
        using var limiter = PulseRateLimits.CreateGlobalLimiter();

        Allowed(limiter, () => Request("GET", "/api/v1/meta"), PulseRateLimits.PerUserPerMinute + 50)
            .Should().Be(PulseRateLimits.PerUserPerMinute + 50);
    }
}
