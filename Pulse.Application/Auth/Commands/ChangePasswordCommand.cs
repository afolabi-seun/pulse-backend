using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Auth.Commands;

public record ChangePasswordCommand(Guid UserId, string CurrentPassword, string NewPassword, string? IpAddress)
    : IRequest<ServiceResult<Unit>>;

public class ChangePasswordHandler : IRequestHandler<ChangePasswordCommand, ServiceResult<Unit>>
{
    private readonly IEngineerRepository _engineers;
    private readonly IPasswordHasher _hasher;
    private readonly IBreachedPasswordChecker _hibp;
    private readonly IAuditLogRepository _audit;

    public ChangePasswordHandler(
        IEngineerRepository engineers,
        IPasswordHasher hasher,
        IBreachedPasswordChecker hibp,
        IAuditLogRepository audit)
    {
        _engineers = engineers;
        _hasher = hasher;
        _hibp = hibp;
        _audit = audit;
    }

    public async Task<ServiceResult<Unit>> Handle(ChangePasswordCommand cmd, CancellationToken ct)
    {
        var engineer = await _engineers.GetByIdAsync(cmd.UserId, ct);
        if (engineer is null)
            return ServiceResult<Unit>.Fail("NOT_FOUND", "User not found.");

        if (!_hasher.Verify(cmd.CurrentPassword, engineer.PasswordHash))
            return ServiceResult<Unit>.Fail("WRONG_PASSWORD", "Current password is incorrect.");

        if (cmd.NewPassword.Length < 12)
            return ServiceResult<Unit>.Fail("PASSWORD_TOO_SHORT", "New password must be at least 12 characters.");

        if (await _hibp.IsBreachedAsync(cmd.NewPassword, ct))
            return ServiceResult<Unit>.Fail("BREACHED_PASSWORD",
                "This password has appeared in a data breach. Please choose a different password.");

        engineer.SetPasswordHash(_hasher.Hash(cmd.NewPassword));
        await _engineers.SaveChangesAsync(ct);
        await _audit.LogAsync("AUTH_PASSWORD_CHANGED", engineer.Id, cmd.IpAddress, ct: ct);

        return ServiceResult<Unit>.Ok(Unit.Value);
    }
}
