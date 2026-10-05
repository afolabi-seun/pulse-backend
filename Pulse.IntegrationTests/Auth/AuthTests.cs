using System.Net;
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

[Collection("Integration")]
public class AuthTests : IClassFixture<PulseWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly PulseWebApplicationFactory _factory;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public AuthTests(PulseWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private async Task<Engineer> SeedEngineerAsync(string email = "test@pulse.io", string role = Roles.Engineer)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<Application.Common.Interfaces.IPasswordHasher>();

        var engineer = Engineer.Create("Test User", email, hasher.Hash("Str0ng!Pass12"), role, 20, 14);
        db.Engineers.Add(engineer);
        await db.SaveChangesAsync();
        return engineer;
    }

    private async Task<ApiResponse<AuthDto>?> LoginAsync(string email, string password)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auth/login",
            new { email, password });
        var body = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<ApiResponse<AuthDto>>(body, JsonOpts);
    }

    // ── login ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Login_returns_200_with_tokens_for_valid_credentials()
    {
        await SeedEngineerAsync("login_ok@pulse.io");

        var result = await LoginAsync("login_ok@pulse.io", "Str0ng!Pass12");

        result!.Status.Should().Be("success");
        result.Data!.AccessToken.Should().NotBeNullOrEmpty();
        result.Data.RefreshToken.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Login_returns_401_for_wrong_password()
    {
        await SeedEngineerAsync("login_wrong@pulse.io");

        var response = await _client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = "login_wrong@pulse.io", password = "wrong" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_returns_401_for_unknown_email()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = "nobody@pulse.io", password = "Str0ng!Pass12" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_locks_account_after_five_consecutive_failures()
    {
        await SeedEngineerAsync("lockout@pulse.io");

        for (var i = 0; i < 5; i++)
            await _client.PostAsJsonAsync("/api/v1/auth/login",
                new { email = "lockout@pulse.io", password = "wrong" });

        var response = await _client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = "lockout@pulse.io", password = "Str0ng!Pass12" });

        var body = await response.Content.ReadFromJsonAsync<ApiResponse<object>>(JsonOpts);
        body!.Error!.Code.Should().Be("ACCOUNT_LOCKED");
    }

    [Fact]
    public async Task Login_returns_400_when_email_is_missing()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auth/login",
            new { password = "Str0ng!Pass12" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── refresh ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Refresh_returns_new_tokens_for_valid_refresh_token()
    {
        await SeedEngineerAsync("refresh_ok@pulse.io");
        var login = await LoginAsync("refresh_ok@pulse.io", "Str0ng!Pass12");

        var response = await _client.PostAsJsonAsync("/api/v1/auth/refresh",
            new { refreshToken = login!.Data!.RefreshToken });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<AuthDto>>(JsonOpts);
        result!.Data!.RefreshToken.Should().NotBe(login.Data.RefreshToken);
    }

    [Fact]
    public async Task Refresh_returns_401_for_unknown_token()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auth/refresh",
            new { refreshToken = "completely_fake_token" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_replay_revokes_all_sessions_and_returns_401()
    {
        await SeedEngineerAsync("replay@pulse.io");
        var login = await LoginAsync("replay@pulse.io", "Str0ng!Pass12");
        var token = login!.Data!.RefreshToken;

        // First refresh — rotates the token
        await _client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = token });

        // Second use of the original (now revoked) token — replay attack
        var response = await _client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = token });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── logout ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Logout_returns_200_and_invalidates_refresh_token()
    {
        await SeedEngineerAsync("logout@pulse.io");
        var login = await LoginAsync("logout@pulse.io", "Str0ng!Pass12");

        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", login!.Data!.AccessToken);

        var logoutResp = await _client.PostAsJsonAsync("/api/v1/auth/logout",
            new { refreshToken = login.Data.RefreshToken });

        logoutResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // Refresh after logout should fail
        var refreshResp = await _client.PostAsJsonAsync("/api/v1/auth/refresh",
            new { refreshToken = login.Data.RefreshToken });

        refreshResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_is_idempotent_for_already_revoked_token()
    {
        await SeedEngineerAsync("logout_idem@pulse.io");
        var login = await LoginAsync("logout_idem@pulse.io", "Str0ng!Pass12");

        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", login!.Data!.AccessToken);

        await _client.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = login.Data.RefreshToken });
        var second = await _client.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = login.Data.RefreshToken });

        second.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── password reset ────────────────────────────────────────────────────────

    [Fact]
    public async Task InitiatePasswordReset_returns_200_for_known_email()
    {
        await SeedEngineerAsync("reset_init@pulse.io");

        var response = await _client.PostAsJsonAsync("/api/v1/auth/password-reset",
            new { email = "reset_init@pulse.io" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task InitiatePasswordReset_returns_200_for_unknown_email_preventing_enumeration()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auth/password-reset",
            new { email = "nobody@pulse.io" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ConfirmPasswordReset_returns_400_for_invalid_token()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auth/password-reset/confirm",
            new { token = "bad_token", newPassword = "Str0ng!Pass12" });

        var body = await response.Content.ReadFromJsonAsync<ApiResponse<object>>(JsonOpts);
        body!.Error!.Code.Should().Be("INVALID_TOKEN");
    }

    [Fact]
    public async Task ConfirmPasswordReset_returns_400_when_new_password_too_short()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auth/password-reset/confirm",
            new { token = "any", newPassword = "short" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── me ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Me_returns_401_when_unauthenticated()
    {
        var response = await _client.GetAsync("/api/v1/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Me_returns_current_role_and_capabilities()
    {
        await SeedEngineerAsync("me_ok@pulse.io", Roles.ProjectManager);
        var login = await LoginAsync("me_ok@pulse.io", "Str0ng!Pass12");
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", login!.Data!.AccessToken);

        var response = await _client.GetAsync("/api/v1/auth/me");
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<AuthUserDto>>(JsonOpts);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        result!.Data!.Role.Should().Be(Roles.ProjectManager);
        result.Data.Capabilities.Should().Contain("pm-or-above");
        result.Data.Capabilities.Should().NotContain("any-head");
    }

    [Fact]
    public async Task Me_reflects_a_role_change_made_after_login_without_a_fresh_login()
    {
        var engineer = await SeedEngineerAsync("me_rolechange@pulse.io", Roles.Engineer);
        var login = await LoginAsync("me_rolechange@pulse.io", "Str0ng!Pass12");
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", login!.Data!.AccessToken);

        var before = await _client.GetFromJsonAsync<ApiResponse<AuthUserDto>>("/api/v1/auth/me", JsonOpts);
        before!.Data!.Capabilities.Should().NotContain("any-head");

        // Change the role directly in the DB — mirrors what UpdateUserCommand does, without
        // needing a second, admin-authenticated client just to exercise /auth/me.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var row = await db.Engineers.FindAsync(engineer.Id);
            row!.UpdateRole(Roles.HeadOfRnD);
            await db.SaveChangesAsync();
        }

        var after = await _client.GetFromJsonAsync<ApiResponse<AuthUserDto>>("/api/v1/auth/me", JsonOpts);

        after!.Data!.Role.Should().Be(Roles.HeadOfRnD);
        after.Data.Capabilities.Should().Contain("any-head");
    }

    // ── full flow ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Full_login_refresh_logout_flow()
    {
        await SeedEngineerAsync("full_flow@pulse.io");

        // 1 — login
        var login = await LoginAsync("full_flow@pulse.io", "Str0ng!Pass12");
        login!.Data!.AccessToken.Should().NotBeNullOrEmpty();

        // 2 — refresh
        var refreshResp = await _client.PostAsJsonAsync("/api/v1/auth/refresh",
            new { refreshToken = login.Data.RefreshToken });
        var refresh = await refreshResp.Content.ReadFromJsonAsync<ApiResponse<AuthDto>>(JsonOpts);
        refresh!.Data!.AccessToken.Should().NotBeNullOrEmpty();

        // 3 — logout
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", refresh.Data.AccessToken);

        var logout = await _client.PostAsJsonAsync("/api/v1/auth/logout",
            new { refreshToken = refresh.Data.RefreshToken });
        logout.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
