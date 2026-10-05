using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Auth.Commands;

public record LogoutCommand(string Token, Guid ActorId, string? IpAddress) : IRequest<ServiceResult<bool>>;

/// <summary>
/// Revokes the supplied refresh token. Idempotent — returns success even if the token is
/// unknown or already revoked, so clients can always treat 200 as "logged out."
/// </summary>
public class LogoutHandler : IRequestHandler<LogoutCommand, ServiceResult<bool>>
{
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IJwtService _jwt;
    private readonly IAuditLogRepository _audit;

    public LogoutHandler(IRefreshTokenRepository refreshTokens, IJwtService jwt, IAuditLogRepository audit)
    {
        _refreshTokens = refreshTokens;
        _jwt = jwt;
        _audit = audit;
    }

    public async Task<ServiceResult<bool>> Handle(LogoutCommand cmd, CancellationToken ct)
    {
        var tokenHash = _jwt.HashToken(cmd.Token);
        var stored = await _refreshTokens.GetByHashAsync(tokenHash, ct);

        if (stored is not null && !stored.IsRevoked)
        {
            stored.RevokeAll();
            await _refreshTokens.SaveChangesAsync(ct);
        }

        await _audit.LogAsync("AUTH_LOGOUT", cmd.ActorId, cmd.IpAddress, ct: ct);

        return ServiceResult<bool>.Ok(true);
    }
}
