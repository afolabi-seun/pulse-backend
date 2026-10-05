using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Pulse.Application.Auth;
using Pulse.Application.Common;
using Pulse.Domain.Engineers;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Auth;

/// <summary>
/// The refresh token lives in an httpOnly cookie, not in script-readable storage — see RefreshTokenCookie. The test
/// client does not replay cookies over http (they are Secure), so the cookie is read from Set-Cookie and sent back by hand.
/// </summary>
[Collection("Integration")]
public class RefreshCookieTests : IClassFixture<PulseWebApplicationFactory>
{
    private const string Password = "Str0ng!Pass12";
    private const string Client = "X-Pulse-Client";

    private readonly HttpClient _client;
    private readonly PulseWebApplicationFactory _factory;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public RefreshCookieTests(PulseWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task SeedAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<Application.Common.Interfaces.IPasswordHasher>();
        db.Engineers.Add(Engineer.Create("Cookie User", email, hasher.Hash(Password), Roles.Engineer, 20, 14));
        await db.SaveChangesAsync();
    }

    private static string? SetCookieHeader(HttpResponseMessage r) =>
        r.Headers.TryGetValues("Set-Cookie", out var v) ? v.FirstOrDefault(c => c.StartsWith("pulse_refresh=")) : null;

    private static string CookieValue(string setCookie) => setCookie.Split(';')[0]["pulse_refresh=".Length..];

    private async Task<(HttpResponseMessage Response, ApiResponse<AuthDto>? Body)> LoginAsync(string email, bool useCookie)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password, useCookie });
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<AuthDto>>(JsonOpts);
        return (response, body);
    }

    private Task<HttpResponseMessage> RefreshWithCookieAsync(string token, bool withClientHeader = true)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh") { Content = JsonContent.Create(new { }) };
        req.Headers.Add("Cookie", $"pulse_refresh={token}");
        if (withClientHeader) req.Headers.Add(Client, "web");
        return _client.SendAsync(req);
    }

    [Fact]
    public async Task Login_sets_an_httpOnly_strict_cookie_scoped_to_the_auth_endpoints()
    {
        await SeedAsync("cookie_flags@cadence.io");

        var (response, _) = await LoginAsync("cookie_flags@cadence.io", useCookie: true);

        var cookie = SetCookieHeader(response);
        cookie.Should().NotBeNull();
        cookie!.ToLowerInvariant().Should().Contain("httponly").And.Contain("samesite=strict").And.Contain("path=/api/v1/auth");
    }

    [Fact]
    public async Task A_cookie_login_never_puts_the_refresh_token_in_the_body()
    {
        await SeedAsync("cookie_nobody@cadence.io");

        var (_, body) = await LoginAsync("cookie_nobody@cadence.io", useCookie: true);

        body!.Data!.AccessToken.Should().NotBeNullOrEmpty();
        body.Data.RefreshToken.Should().BeEmpty();
    }

    [Fact]
    public async Task A_login_that_does_not_ask_for_the_cookie_still_gets_the_token_in_the_body()
    {
        // Web builds from before the cookie existed, and any other API caller.
        await SeedAsync("cookie_legacy@cadence.io");

        var (response, body) = await LoginAsync("cookie_legacy@cadence.io", useCookie: false);

        body!.Data!.RefreshToken.Should().NotBeNullOrEmpty();
        SetCookieHeader(response).Should().NotBeNull("it moves to the cookie from the first login");
    }

    [Fact]
    public async Task Refresh_with_the_cookie_rotates_it_and_keeps_the_new_token_out_of_the_body()
    {
        await SeedAsync("cookie_refresh@cadence.io");
        var (login, _) = await LoginAsync("cookie_refresh@cadence.io", useCookie: true);
        var first = CookieValue(SetCookieHeader(login)!);

        var response = await RefreshWithCookieAsync(first);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<AuthDto>>(JsonOpts);
        body!.Data!.AccessToken.Should().NotBeNullOrEmpty();
        body.Data.RefreshToken.Should().BeEmpty("a script that calls refresh must not be able to read the token");
        var second = CookieValue(SetCookieHeader(response)!);
        second.Should().NotBe(first);
    }

    [Fact]
    public async Task Refresh_with_the_cookie_but_without_the_client_header_is_refused()
    {
        await SeedAsync("cookie_noheader@cadence.io");
        var (login, _) = await LoginAsync("cookie_noheader@cadence.io", useCookie: true);

        var response = await RefreshWithCookieAsync(CookieValue(SetCookieHeader(login)!), withClientHeader: false);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Refresh_with_no_cookie_and_no_body_token_is_unauthorized()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auth/refresh", new { });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_legacy_body_token_still_refreshes_and_moves_the_session_to_the_cookie()
    {
        // A signed-in session from before the cookie existed: one refresh moves it over.
        await SeedAsync("cookie_migrate@cadence.io");
        var (_, legacy) = await LoginAsync("cookie_migrate@cadence.io", useCookie: false);

        var response = await _client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = legacy!.Data!.RefreshToken });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<AuthDto>>(JsonOpts);
        body!.Data!.RefreshToken.Should().NotBeNullOrEmpty().And.NotBe(legacy.Data.RefreshToken);
        SetCookieHeader(response).Should().NotBeNull();
    }

    [Fact]
    public async Task A_failed_refresh_clears_the_cookie()
    {
        var response = await RefreshWithCookieAsync("not-a-real-token");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var cleared = SetCookieHeader(response);
        cleared.Should().NotBeNull();
        CookieValue(cleared!).Should().BeEmpty();
    }

    [Fact]
    public async Task Logout_with_the_cookie_revokes_the_token_and_clears_the_cookie()
    {
        await SeedAsync("cookie_logout@cadence.io");
        var (login, body) = await LoginAsync("cookie_logout@cadence.io", useCookie: true);
        var token = CookieValue(SetCookieHeader(login)!);

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout") { Content = JsonContent.Create(new { }) };
        req.Headers.Add("Cookie", $"pulse_refresh={token}");
        req.Headers.Add(Client, "web");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", body!.Data!.AccessToken);
        var logout = await _client.SendAsync(req);

        logout.StatusCode.Should().Be(HttpStatusCode.OK);
        CookieValue(SetCookieHeader(logout)!).Should().BeEmpty();
        (await RefreshWithCookieAsync(token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
