using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Auth.Commands;

public record LoginCommand(string Email, string Password, string? IpAddress) : IRequest<ServiceResult<AuthDto>>;

/// <summary>
/// Authenticates an engineer by email and password and issues JWT + refresh token pair.
/// Enforces account lockout: 5 consecutive failures triggers a 15-minute lock.
/// </summary>
public class LoginHandler : IRequestHandler<LoginCommand, ServiceResult<AuthDto>>
{
    private readonly IEngineerRepository _engineers;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IPasswordHasher _hasher;
    private readonly IJwtService _jwt;
    private readonly IAuditLogRepository _audit;
    private readonly IEmailQueue _emailQueue;
    private readonly IAppSettings _settings;

    public LoginHandler(
        IEngineerRepository engineers,
        IRefreshTokenRepository refreshTokens,
        IPasswordHasher hasher,
        IJwtService jwt,
        IAuditLogRepository audit,
        IEmailQueue emailQueue,
        IAppSettings settings)
    {
        _engineers = engineers;
        _refreshTokens = refreshTokens;
        _hasher = hasher;
        _jwt = jwt;
        _audit = audit;
        _emailQueue = emailQueue;
        _settings = settings;
    }

    public async Task<ServiceResult<AuthDto>> Handle(LoginCommand cmd, CancellationToken ct)
    {
        var engineer = await _engineers.GetByEmailAsync(cmd.Email, ct);

        // Constant-time-equivalent: always verify hash even when engineer not found to avoid timing leaks
        if (engineer is null)
        {
            _hasher.Verify(cmd.Password, "$argon2id$v=19$m=65536,t=3,p=4$dummy$dummy");
            return ServiceResult<AuthDto>.Fail("UNAUTHORIZED", "Invalid credentials.");
        }

        if (engineer.IsLockedOut())
        {
            await _audit.LogAsync("AUTH_LOCKOUT_BLOCKED", engineer.Id, cmd.IpAddress, ct: ct);
            return ServiceResult<AuthDto>.Fail("ACCOUNT_LOCKED", "Account is temporarily locked. Try again later.");
        }

        if (!_hasher.Verify(cmd.Password, engineer.PasswordHash))
        {
            engineer.RecordFailedLogin();
            await _engineers.SaveChangesAsync(ct);
            await _audit.LogAsync("AUTH_FAILED", engineer.Id, cmd.IpAddress, ct: ct);

            if (engineer.IsLockedOut())
            {
                var resetLink = $"{_settings.AppBaseUrl}/forgot-password";
                var body = $"""
                    <p>Hi {engineer.Name},</p>
                    <p>Your Pulse account has been temporarily locked after 5 consecutive failed login attempts.</p>
                    <p>The lock will lift in 15 minutes. If you forgot your password, you can reset it now:</p>
                    {EmailTemplate.Button(resetLink, "Reset password")}
                    {EmailTemplate.Muted("If you did not attempt to log in, contact your department head immediately.")}
                    """;
                _emailQueue.Enqueue(engineer.Email, "Pulse — account locked", EmailTemplate.Layout(body));
            }

            return ServiceResult<AuthDto>.Fail("UNAUTHORIZED", "Invalid credentials.");
        }

        engineer.ResetLoginAttempts();

        var rawRefreshToken = _jwt.GenerateRefreshToken();
        var tokenHash = _jwt.HashToken(rawRefreshToken);
        var refreshToken = Domain.Engineers.RefreshToken.Create(engineer.Id, tokenHash, DateTime.UtcNow.AddDays(14));

        await _refreshTokens.AddAsync(refreshToken, ct);
        await _engineers.SaveChangesAsync(ct);
        await _audit.LogAsync("AUTH_LOGIN", engineer.Id, cmd.IpAddress, ct: ct);

        var accessToken = _jwt.GenerateAccessToken(engineer);

        return ServiceResult<AuthDto>.Ok(new AuthDto(
            accessToken,
            rawRefreshToken,
            new AuthUserDto(engineer.Id, engineer.Name, engineer.Email, engineer.Role,
                PermissionService.For(engineer.Role), CapabilityRegistry.ResolveFor(engineer.Role))));
    }
}
