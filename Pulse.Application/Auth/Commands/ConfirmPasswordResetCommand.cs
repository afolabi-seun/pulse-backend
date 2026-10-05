using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Auth.Commands;

public record ConfirmPasswordResetCommand(string Token, string NewPassword, string? IpAddress) : IRequest<ServiceResult<bool>>;

/// <summary>
/// Validates the reset token, checks the new password against the HIBP breach corpus,
/// updates the password hash, and revokes all existing refresh tokens for the engineer.
/// </summary>
public class ConfirmPasswordResetHandler : IRequestHandler<ConfirmPasswordResetCommand, ServiceResult<bool>>
{
    private readonly IEngineerRepository _engineers;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IPasswordHasher _hasher;
    private readonly IJwtService _jwt;
    private readonly IBreachedPasswordChecker _hibp;
    private readonly IAuditLogRepository _audit;

    public ConfirmPasswordResetHandler(
        IEngineerRepository engineers,
        IRefreshTokenRepository refreshTokens,
        IPasswordHasher hasher,
        IJwtService jwt,
        IBreachedPasswordChecker hibp,
        IAuditLogRepository audit)
    {
        _engineers = engineers;
        _refreshTokens = refreshTokens;
        _hasher = hasher;
        _jwt = jwt;
        _hibp = hibp;
        _audit = audit;
    }

    public async Task<ServiceResult<bool>> Handle(ConfirmPasswordResetCommand cmd, CancellationToken ct)
    {
        var tokenHash = _jwt.HashToken(cmd.Token);
        var engineer = await _engineers.GetByResetTokenHashAsync(tokenHash, ct);

        if (engineer is null)
            return ServiceResult<bool>.Fail("INVALID_TOKEN", "Reset token is invalid or has expired.");

        if (await _hibp.IsBreachedAsync(cmd.NewPassword, ct))
            return ServiceResult<bool>.Fail("BREACHED_PASSWORD",
                "This password has appeared in a data breach. Please choose a different password.");

        var newHash = _hasher.Hash(cmd.NewPassword);
        engineer.SetPasswordHash(newHash);

        await _refreshTokens.RevokeAllForEngineerAsync(engineer.Id, ct);
        await _engineers.SaveChangesAsync(ct);
        await _audit.LogAsync("AUTH_PASSWORD_RESET_CONFIRMED", engineer.Id, cmd.IpAddress, ct: ct);

        return ServiceResult<bool>.Ok(true);
    }
}
