using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Auth.Commands;

public record RefreshTokenCommand(string Token, string? IpAddress) : IRequest<ServiceResult<AuthDto>>;

/// <summary>
/// Rotates a refresh token and issues a new JWT + refresh token pair.
/// If the presented token was already revoked the request is treated as a replay attack:
/// all sessions for that engineer are immediately revoked.
/// </summary>
public class RefreshTokenHandler : IRequestHandler<RefreshTokenCommand, ServiceResult<AuthDto>>
{
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IEngineerRepository _engineers;
    private readonly IJwtService _jwt;
    private readonly IAuditLogRepository _audit;
    private readonly IAppSettings _settings;

    public RefreshTokenHandler(
        IRefreshTokenRepository refreshTokens,
        IEngineerRepository engineers,
        IJwtService jwt,
        IAuditLogRepository audit,
        IAppSettings settings)
    {
        _refreshTokens = refreshTokens;
        _engineers = engineers;
        _jwt = jwt;
        _audit = audit;
        _settings = settings;
    }

    public async Task<ServiceResult<AuthDto>> Handle(RefreshTokenCommand cmd, CancellationToken ct)
    {
        var tokenHash = _jwt.HashToken(cmd.Token);
        var stored = await _refreshTokens.GetByHashAsync(tokenHash, ct);

        if (stored is null || stored.IsExpired)
            return ServiceResult<AuthDto>.Fail("UNAUTHORIZED", "Invalid or expired refresh token.");

        if (!stored.IsRevoked && stored.IsIdleExpired(TimeSpan.FromMinutes(_settings.RefreshTokenIdleTimeoutMinutes)))
        {
            await _audit.LogAsync("AUTH_IDLE_TIMEOUT", stored.EngineerID, cmd.IpAddress, ct: ct);
            return ServiceResult<AuthDto>.Fail("UNAUTHORIZED", "Session expired due to inactivity. Please log in again.");
        }

        if (stored.IsRevoked)
        {
            // Replay attack detected — revoke every session for this engineer
            await _refreshTokens.RevokeAllForEngineerAsync(stored.EngineerID, ct);
            await _refreshTokens.SaveChangesAsync(ct);
            await _audit.LogAsync("AUTH_REPLAY_ATTACK", stored.EngineerID, cmd.IpAddress, ct: ct);
            return ServiceResult<AuthDto>.Fail("UNAUTHORIZED", "Invalid or expired refresh token.");
        }

        var engineer = await _engineers.GetByIdAsync(stored.EngineerID, ct);
        if (engineer is null || !engineer.IsActive)
            return ServiceResult<AuthDto>.Fail("UNAUTHORIZED", "Invalid or expired refresh token.");

        var rawNewToken = _jwt.GenerateRefreshToken();
        var newHash = _jwt.HashToken(rawNewToken);

        var newRefreshToken = stored.Rotate(newHash);
        await _refreshTokens.AddAsync(newRefreshToken, ct);
        await _refreshTokens.SaveChangesAsync(ct);
        await _audit.LogAsync("AUTH_REFRESH", engineer.Id, cmd.IpAddress, ct: ct);

        var accessToken = _jwt.GenerateAccessToken(engineer);

        return ServiceResult<AuthDto>.Ok(new AuthDto(
            accessToken,
            rawNewToken,
            new AuthUserDto(engineer.Id, engineer.Name, engineer.Email, engineer.Role,
                PermissionService.For(engineer.Role), CapabilityRegistry.ResolveFor(engineer.Role))));
    }
}
