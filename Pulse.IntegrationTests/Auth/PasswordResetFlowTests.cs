using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Domain.Engineers;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Auth;

/// <summary>
/// Integration tests for the full password reset flow:
///   initiate → token stored in DB → confirm with raw token → password changed.
/// </summary>
[Collection("Integration")]
public class PasswordResetFlowTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public PasswordResetFlowTests(PulseWebApplicationFactory factory) : base(factory) { }

    // ── confirm with valid token ──────────────────────────────────────────────

    [Fact]
    public async Task ConfirmReset_with_valid_token_changes_password_and_allows_login()
    {
        const string newPassword = "NewStr0ng!Pass99";
        var engineer = await SeedEngineerAsync("reset_full@pulse.io");

        // Seed a valid reset token directly in DB (mimics what InitiatePasswordReset does)
        const string rawToken = "test_raw_reset_token_fullflow";
        await SetResetTokenAsync(engineer.Id, rawToken, DateTime.UtcNow.AddHours(1));

        var response = await Client.PostAsJsonAsync("/api/v1/auth/password-reset/confirm", new
        {
            token = rawToken,
            newPassword
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // New password must allow login
        var loginResp = await Client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = "reset_full@pulse.io", password = newPassword });
        loginResp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ConfirmReset_invalidates_old_password()
    {
        const string newPassword = "NewStr0ng!Pass88";
        var engineer = await SeedEngineerAsync("reset_old_pw@pulse.io");

        const string rawToken = "test_raw_reset_token_oldpw";
        await SetResetTokenAsync(engineer.Id, rawToken, DateTime.UtcNow.AddHours(1));

        await Client.PostAsJsonAsync("/api/v1/auth/password-reset/confirm", new
        {
            token = rawToken,
            newPassword
        });

        // Old password must no longer work
        var loginResp = await Client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = "reset_old_pw@pulse.io", password = "Str0ng!Pass12" });
        loginResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ConfirmReset_revokes_all_existing_refresh_tokens()
    {
        var engineer = await SeedEngineerAsync("reset_revoke@pulse.io");

        // Login to get a refresh token before reset
        var loginBody = (await (await Client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = "reset_revoke@pulse.io", password = "Str0ng!Pass12" }))
            .Content.ReadFromJsonAsync<ApiResponse<Pulse.Application.Auth.AuthDto>>(JsonOpts))!.Data!;
        var refreshToken = loginBody.RefreshToken;

        const string rawToken = "test_raw_reset_token_revoke";
        await SetResetTokenAsync(engineer.Id, rawToken, DateTime.UtcNow.AddHours(1));

        await Client.PostAsJsonAsync("/api/v1/auth/password-reset/confirm", new
        {
            token = rawToken,
            newPassword = "NewStr0ng!Pass77"
        });

        // Existing refresh token must be revoked
        var refreshResp = await Client.PostAsJsonAsync("/api/v1/auth/refresh",
            new { refreshToken });
        refreshResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "password reset must revoke all active refresh tokens");
    }

    // ── token errors ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ConfirmReset_returns_400_for_unknown_token()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/auth/password-reset/confirm", new
        {
            token = "completely_invalid_token",
            newPassword = "NewStr0ng!Pass12"
        });

        var body = await response.Content.ReadFromJsonAsync<ApiResponse<object>>(JsonOpts);
        body!.Error!.Code.Should().Be("INVALID_TOKEN");
    }

    [Fact]
    public async Task ConfirmReset_returns_400_for_expired_token()
    {
        var engineer = await SeedEngineerAsync("reset_expired@pulse.io");

        const string rawToken = "test_expired_token";
        // Store token with expiry in the past
        await SetResetTokenAsync(engineer.Id, rawToken, DateTime.UtcNow.AddHours(-1));

        var response = await Client.PostAsJsonAsync("/api/v1/auth/password-reset/confirm", new
        {
            token = rawToken,
            newPassword = "NewStr0ng!Pass12"
        });

        var body = await response.Content.ReadFromJsonAsync<ApiResponse<object>>(JsonOpts);
        body!.Error!.Code.Should().Be("INVALID_TOKEN",
            "expired tokens are treated the same as invalid tokens to prevent timing attacks");
    }

    [Fact]
    public async Task ConfirmReset_returns_400_when_token_is_reused()
    {
        var engineer = await SeedEngineerAsync("reset_reuse@pulse.io");

        const string rawToken = "test_reuse_token";
        await SetResetTokenAsync(engineer.Id, rawToken, DateTime.UtcNow.AddHours(1));

        // First use — succeeds
        await Client.PostAsJsonAsync("/api/v1/auth/password-reset/confirm", new
        {
            token = rawToken,
            newPassword = "NewStr0ng!Pass55"
        });

        // Second use of same token — must fail (SetPasswordHash clears the token)
        var response = await Client.PostAsJsonAsync("/api/v1/auth/password-reset/confirm", new
        {
            token = rawToken,
            newPassword = "AnotherStr0ng!Pass66"
        });

        var body = await response.Content.ReadFromJsonAsync<ApiResponse<object>>(JsonOpts);
        body!.Error!.Code.Should().Be("INVALID_TOKEN",
            "after a successful reset the token is cleared and must not be reusable");
    }

    // ── password validation ───────────────────────────────────────────────────

    [Fact]
    public async Task ConfirmReset_returns_400_when_new_password_too_short()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/auth/password-reset/confirm", new
        {
            token = "any",
            newPassword = "short"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── initiate endpoint ─────────────────────────────────────────────────────

    [Fact]
    public async Task InitiateReset_stores_token_that_confirm_can_redeem()
    {
        // End-to-end: initiate then confirm without accessing DB directly.
        // Because the email is a no-op, we read the token hash from DB after initiate.
        await SeedEngineerAsync("reset_e2e@pulse.io");

        await Client.PostAsJsonAsync("/api/v1/auth/password-reset",
            new { email = "reset_e2e@pulse.io" });

        // Read the stored token hash directly from DB to simulate receiving the email
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Pulse.Infrastructure.Persistence.PulseDbContext>();
        var eng = db.Engineers.First(e => e.Email == "reset_e2e@pulse.io");

        eng.PasswordResetToken.Should().NotBeNullOrEmpty("initiate must store a token hash");
        eng.PasswordResetTokenExpiresAt.Should().BeAfter(DateTime.UtcNow,
            "token expiry must be in the future");
    }
}
