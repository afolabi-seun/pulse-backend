using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Users.Commands;

public record DeleteUserCommand(Guid UserId, Guid ActorId, string? IpAddress) : IRequest<ServiceResult<bool>>;

/// <summary>
/// Soft-deletes a user by deactivating their account. All historical data is preserved.
/// An actor cannot delete their own account.
/// </summary>
public class DeleteUserHandler : IRequestHandler<DeleteUserCommand, ServiceResult<bool>>
{
    private readonly IEngineerRepository _engineers;
    private readonly IAuditLogRepository _audit;

    public DeleteUserHandler(IEngineerRepository engineers, IAuditLogRepository audit)
    {
        _engineers = engineers;
        _audit = audit;
    }

    public async Task<ServiceResult<bool>> Handle(DeleteUserCommand cmd, CancellationToken ct)
    {
        if (cmd.UserId == cmd.ActorId)
            return ServiceResult<bool>.Fail("BUSINESS_RULE_VIOLATION", "You cannot delete your own account.");

        var engineer = await _engineers.GetByIdAsync(cmd.UserId, ct);
        if (engineer is null)
            return ServiceResult<bool>.Fail("NOT_FOUND", $"User '{cmd.UserId}' not found.");

        if (!engineer.IsActive)
            return ServiceResult<bool>.Ok(true); // idempotent

        engineer.Deactivate();
        await _engineers.SaveChangesAsync(ct);
        await _audit.LogAsync("USER_DELETED", cmd.ActorId, cmd.IpAddress,
            $"User {cmd.UserId} ({engineer.Email}) soft-deleted", ct);

        return ServiceResult<bool>.Ok(true);
    }
}
