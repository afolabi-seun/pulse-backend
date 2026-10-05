using System.Security.Claims;
using Asp.Versioning;
using Pulse.Api.Attributes;
using Pulse.Api.Common;
using Pulse.Api.Security;
using Pulse.Application.Auth;
using Pulse.Application.Auth.Commands;
using Pulse.Application.Auth.Queries;
using Pulse.Application.Common;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Unit = MediatR.Unit;

namespace Pulse.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/auth")]
[Tags("Auth")]
public class AuthController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly bool _secureCookie;

    // The refresh cookie is Secure everywhere except local development, where the API is served over plain http.
    public AuthController(IMediator mediator, IHostEnvironment env)
    {
        _mediator = mediator;
        _secureCookie = !env.IsDevelopment();
    }

    // ── Request records ────────────────────────────────────────────────────────

    public record BootstrapRequest(string Name, string Email, string Password, bool UseCookie = false);
    /// <param name="UseCookie">True from the web app: the refresh token travels only in an httpOnly cookie, never in the response
    /// body. Other callers (and web builds from before the cookie existed) leave it false and still get it in the body.</param>
    public record LoginRequest(string Email, string Password, bool UseCookie = false);
    /// <param name="RefreshToken">Optional: leave it out to use the cookie. A body token is the legacy path, kept so a session started
    /// before the cookie existed can move over once.</param>
    public record RefreshTokenRequest(string? RefreshToken = null);
    public record LogoutRequest(string? RefreshToken = null);
    public record InitiatePasswordResetRequest(string Email);
    public record ConfirmPasswordResetRequest(string Token, string NewPassword);
    public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

    // ── Endpoints ──────────────────────────────────────────────────────────────

    /// <summary>Creates the first Head of R&amp;D account. Only succeeds when no users exist yet.</summary>
    /// <remarks>Returns 409 Conflict if any engineer account already exists.</remarks>
    [HttpPost("bootstrap")]
    [ProducesResponseType(typeof(ApiResponse<Application.Auth.AuthDto>), 201)]
    [ProducesResponseType(typeof(ApiResponse<object>), 400)]
    [ProducesResponseType(typeof(ApiResponse<object>), 409)]
    public async Task<IActionResult> Bootstrap([FromBody] BootstrapRequest request)
    {
        var result = await _mediator.Send(new BootstrapCommand(request.Name, request.Email, request.Password, GetIp()));
        if (!result.IsSuccess) return result.ToCreatedResult();
        RefreshTokenCookie.Write(Response, result.Data!.RefreshToken, _secureCookie);
        return (request.UseCookie ? WithoutRefreshToken(result) : result).ToCreatedResult();
    }

    /// <summary>Authenticates an engineer and returns a JWT access token and refresh token.</summary>
    /// <remarks>
    /// Accounts lock after 5 consecutive failed attempts for 15 minutes.
    /// Tokens: access token valid 30 minutes, refresh token valid 14 days (absolute — no sliding window).
    /// </remarks>
    [HttpPost("login")]
    [ProducesResponseType(typeof(ApiResponse<Application.Auth.AuthDto>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 400)]
    [ProducesResponseType(typeof(ApiResponse<object>), 401)]
    [ProducesResponseType(429)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var result = await _mediator.Send(new LoginCommand(request.Email, request.Password, GetIp()));
        if (!result.IsSuccess) return result.ToActionResult();

        RefreshTokenCookie.Write(Response, result.Data!.RefreshToken, _secureCookie);
        return (request.UseCookie ? WithoutRefreshToken(result) : result).ToActionResult();
    }

    /// <summary>Rotates a refresh token and returns a new JWT + refresh token pair.</summary>
    /// <remarks>
    /// Presenting a previously revoked token is treated as a replay attack:
    /// all sessions for that engineer are immediately revoked.
    /// </remarks>
    [HttpPost("refresh")]
    [ProducesResponseType(typeof(ApiResponse<Application.Auth.AuthDto>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 400)]
    [ProducesResponseType(typeof(ApiResponse<object>), 401)]
    public async Task<IActionResult> Refresh([FromBody] RefreshTokenRequest request)
    {
        // A token in the body is the legacy path; otherwise it comes from the cookie, and then the request must prove it
        // is from the web app (see RefreshTokenCookie).
        var fromCookie = string.IsNullOrEmpty(request.RefreshToken);
        var token = fromCookie ? RefreshTokenCookie.Read(Request) : request.RefreshToken;
        if (string.IsNullOrEmpty(token))
            return ServiceResult<AuthDto>.Fail("UNAUTHORIZED", "No refresh token.").ToActionResult();
        if (fromCookie && !RefreshTokenCookie.HasClientHeader(Request))
            return ServiceResult<AuthDto>.Fail("FORBIDDEN", "Missing client header.").ToActionResult();

        var result = await _mediator.Send(new RefreshTokenCommand(token, GetIp()));
        if (!result.IsSuccess)
        {
            RefreshTokenCookie.Clear(Response, _secureCookie);
            return result.ToActionResult();
        }

        RefreshTokenCookie.Write(Response, result.Data!.RefreshToken, _secureCookie);
        // A cookie-only caller never receives the token in a body: a script on the page could otherwise call this endpoint and
        // read it. The legacy body path keeps it so an older web build keeps working until it has moved to the cookie.
        return (fromCookie ? WithoutRefreshToken(result) : result).ToActionResult();
    }

    /// <summary>Revokes the supplied refresh token, logging the engineer out of that session.</summary>
    /// <remarks>Idempotent — returns 200 even if the token is unknown or already revoked.</remarks>
    [HttpPost("logout")]
    [EngineerAuth]
    [ProducesResponseType(typeof(ApiResponse<object>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 400)]
    [ProducesResponseType(401)]
    public async Task<IActionResult> Logout([FromBody] LogoutRequest request)
    {
        var fromCookie = string.IsNullOrEmpty(request.RefreshToken);
        var token = fromCookie ? RefreshTokenCookie.Read(Request) : request.RefreshToken;
        if (fromCookie && token is not null && !RefreshTokenCookie.HasClientHeader(Request))
            return ServiceResult<Unit>.Fail("FORBIDDEN", "Missing client header.").ToActionResult();

        // Whatever happens to the token, this browser stops holding one.
        RefreshTokenCookie.Clear(Response, _secureCookie);
        if (string.IsNullOrEmpty(token))
            return ServiceResult<Unit>.Ok(Unit.Value).ToActionResult();

        var actorId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        return (await _mediator.Send(new LogoutCommand(token, actorId, GetIp()))).ToActionResult();
    }

    /// <summary>Initiates a password reset by emailing a one-time reset link to the given address.</summary>
    /// <remarks>
    /// Always returns 200 regardless of whether the email is registered — prevents email enumeration.
    /// The reset link expires after 1 hour.
    /// </remarks>
    [HttpPost("password-reset")]
    [ProducesResponseType(typeof(ApiResponse<object>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 400)]
    [ProducesResponseType(429)]
    public async Task<IActionResult> InitiatePasswordReset([FromBody] InitiatePasswordResetRequest request) =>
        (await _mediator.Send(new InitiatePasswordResetCommand(request.Email, GetIp()))).ToActionResult();

    /// <summary>Confirms a password reset using the token from the reset email.</summary>
    /// <remarks>
    /// The new password is checked against the Have I Been Pwned breach corpus.
    /// On success all existing sessions for the engineer are revoked.
    /// Minimum password length: 12 characters.
    /// </remarks>
    [HttpPost("password-reset/confirm")]
    [ProducesResponseType(typeof(ApiResponse<object>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 400)]
    [ProducesResponseType(typeof(ApiResponse<object>), 422)]
    public async Task<IActionResult> ConfirmPasswordReset([FromBody] ConfirmPasswordResetRequest request) =>
        (await _mediator.Send(new ConfirmPasswordResetCommand(request.Token, request.NewPassword, GetIp()))).ToActionResult();

    /// <summary>Changes the authenticated user's password. Requires the current password for verification.</summary>
    [HttpPost("change-password")]
    [EngineerAuth]
    [ProducesResponseType(typeof(ApiResponse<Unit>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<Unit>), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken ct)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        return (await _mediator.Send(new ChangePasswordCommand(userId, request.CurrentPassword, request.NewPassword, GetIp()), ct))
            .ToActionResult();
    }

    /// <summary>Returns the caller's own current role, permissions, and capabilities.</summary>
    /// <remarks>
    /// Lightweight alternative to /auth/refresh — no token rotation. Meant to be called after a
    /// "role.changed" real-time event so a role change made by an admin reaches an already-logged-in
    /// client without forcing a re-login.
    /// </remarks>
    [HttpGet("me")]
    [EngineerAuth]
    [ProducesResponseType(typeof(ApiResponse<Application.Auth.AuthUserDto>), 200)]
    [ProducesResponseType(typeof(ApiResponse<object>), 401)]
    [ProducesResponseType(typeof(ApiResponse<object>), 404)]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        return (await _mediator.Send(new GetMeQuery(userId), ct)).ToActionResult();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static ServiceResult<AuthDto> WithoutRefreshToken(ServiceResult<AuthDto> result) =>
        ServiceResult<AuthDto>.Ok(result.Data! with { RefreshToken = string.Empty });

    private string? GetIp() =>
        HttpContext.Connection.RemoteIpAddress?.ToString();
}
