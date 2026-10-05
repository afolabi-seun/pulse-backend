using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Epics;
using Pulse.Domain.Common;
using MediatR;

namespace Pulse.Application.Tasks.Commands;

public record ClearBlockerCommand(
    Guid TaskId,
    Guid ActorId,
    string? IpAddress,
    string ActorRole = "") : IRequest<ServiceResult<TaskDto>>;

public class ClearBlockerHandler : IRequestHandler<ClearBlockerCommand, ServiceResult<TaskDto>>
{
    private readonly ITaskRepository _tasks;
    private readonly IAuditLogRepository _audit;
    private readonly IEpicRepository _epics;
    private readonly IProjectAccessPolicy _access;

    public ClearBlockerHandler(ITaskRepository tasks, IAuditLogRepository audit, IEpicRepository epics, IProjectAccessPolicy access)
    {
        _tasks = tasks;
        _audit = audit;
        _epics = epics;
        _access = access;
    }

    public async Task<ServiceResult<TaskDto>> Handle(ClearBlockerCommand cmd, CancellationToken ct)
    {
        var task = await _tasks.GetByIdAsync(cmd.TaskId, ct);
        if (task is null)
            return ServiceResult<TaskDto>.Fail("NOT_FOUND", $"Task '{cmd.TaskId}' not found.");

        if (task.AssigneeId != cmd.ActorId
            && !await _access.CanAccessProjectAsync(task.ProjectId, cmd.ActorId, cmd.ActorRole, ct))
            return ServiceResult<TaskDto>.Fail("FORBIDDEN", "You do not have access to this task.");

        try
        {
            task.ClearBlocker(cmd.ActorId);
        }
        catch (DomainException ex)
        {
            return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION", ex.Message);
        }

        await _tasks.SaveChangesAsync(ct);
        await EpicStatusRollUp.ApplyAsync(task.EpicId, _tasks, _epics, ct);

        await _audit.LogAsync("BLOCKER_CLEARED", cmd.ActorId, cmd.IpAddress,
            $"Blocker cleared on task {task.Id}", ct);

        return ServiceResult<TaskDto>.Ok(TaskDto.From(task));
    }
}
