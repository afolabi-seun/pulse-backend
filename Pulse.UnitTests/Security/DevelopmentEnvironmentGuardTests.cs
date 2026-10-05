using Pulse.Api.Configuration;
using Pulse.Api.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Pulse.UnitTests.Security;

public class DevelopmentEnvironmentGuardTests
{
    [Theory]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("host.docker.internal")]
    [InlineData("postgres")]          // a docker-compose service name
    [InlineData("")]
    [InlineData(null)]
    public void Recognises_a_local_database_host(string? host) =>
        DevelopmentEnvironmentGuard.IsLocalHost(host).Should().BeTrue();

    [Theory]
    [InlineData("10.0.0.9")]
    [InlineData("db.internal.example.com")]
    [InlineData("cadence.abc123.eu-west-1.rds.amazonaws.com")]
    [InlineData("203.0.113.5")]
    public void Treats_anything_else_as_a_shared_database(string host) =>
        DevelopmentEnvironmentGuard.IsLocalHost(host).Should().BeFalse();

    [Theory]
    [InlineData("Host=10.0.0.9;Port=5432;Database=cadence;Username=a;Password=b", "10.0.0.9")]
    [InlineData("Host=localhost;Database=cadence_dev;Username=a;Password=b", "localhost")]
    public void Reads_the_host_out_of_a_connection_string(string conn, string expected) =>
        DevelopmentEnvironmentGuard.DatabaseHost(conn).Should().Be(expected);

    // ── The refresh cookie's Secure flag follows who is asking, not only the environment name ──

    private static HttpRequest RequestFrom(string? origin)
    {
        var ctx = new DefaultHttpContext();
        if (origin is not null) ctx.Request.Headers.Origin = origin;
        return ctx.Request;
    }

    [Fact]
    public void The_cookie_is_Secure_in_any_non_development_environment() =>
        RefreshTokenCookie.UseSecure(RequestFrom("http://localhost:5173"), isDevelopment: false).Should().BeTrue();

    [Theory]
    [InlineData("http://localhost:5173")]
    [InlineData("http://127.0.0.1:5173")]
    [InlineData(null)]
    public void Local_development_may_use_a_plain_http_cookie(string? origin) =>
        RefreshTokenCookie.UseSecure(RequestFrom(origin), isDevelopment: true).Should().BeFalse();

    [Fact]
    public void A_server_wrongly_left_in_Development_still_gets_a_Secure_cookie_for_a_real_frontend() =>
        RefreshTokenCookie.UseSecure(RequestFrom("https://cadence-frontend.project-demo.app"), isDevelopment: true).Should().BeTrue();
}
