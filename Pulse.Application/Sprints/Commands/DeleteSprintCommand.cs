using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Sprints.Commands;

public record DeleteSprintCommand(Guid SprintId, Guid ActorId, string? IpAddress, string ActorRole = "") : IRequest<ServiceResult<bool>>;

public class DeleteSprintHandler : IRequestHandler<DeleteSprintCommand, ServiceResult<bool>>
{
    private readonly ISprintRepository _sprints;
    private readonly IAuditLogRepository _audit;
    private readonly IProjectAccessPolicy _access;

    public DeleteSprintHandler(ISprintRepository sprints, IAuditLogRepository audit, IProjectAccessPolicy access)
    {
        _sprints = sprints;
        _audit = audit;
        _access = access;
    }

    public async Task<ServiceResult<bool>> Handle(DeleteSprintCommand cmd, CancellationToken ct)
    {
        var sprint = await _sprints.GetByIdAsync(cmd.SprintId, ct);
        if (sprint is null)
            return ServiceResult<bool>.Fail("NOT_FOUND", "Sprint not found.");

        if (!await _access.CanAccessTeamAsync(sprint.TeamId, cmd.ActorId, cmd.ActorRole, ct))
            return ServiceResult<bool>.Fail("FORBIDDEN", "You do not have access to this sprint.");

        if (sprint.Status != Domain.Sprints.SprintStatus.Planning)
            return ServiceResult<bool>.Fail("BUSINESS_RULE_VIOLATION",
                "Only sprints in Planning status can be deleted.");

        await _sprints.RemoveAsync(sprint, ct);
        await _sprints.SaveChangesAsync(ct);
        await _audit.LogAsync("SPRINT_DELETED", cmd.ActorId, cmd.IpAddress,
            $"Deleted sprint {sprint.Id} ({sprint.Name})", ct);

        return ServiceResult<bool>.Ok(true);
    }
}
