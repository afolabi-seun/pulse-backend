using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Epics;
using Pulse.Domain.Common;
using MediatR;

namespace Pulse.Application.Tasks.Commands;

public record PauseTaskCommand(
    Guid TaskId,
    string? Note,
    Guid ActorId,
    string? IpAddress,
    string ActorRole = "") : IRequest<ServiceResult<TaskDto>>;

public class PauseTaskHandler : IRequestHandler<PauseTaskCommand, ServiceResult<TaskDto>>
{
    private readonly ITaskRepository _tasks;
    private readonly IAuditLogRepository _audit;
    private readonly IRealtimeNotifier _realtime;
    private readonly IEpicRepository _epics;
    private readonly IProjectAccessPolicy _access;

    public PauseTaskHandler(ITaskRepository tasks, IAuditLogRepository audit, IRealtimeNotifier realtime, IEpicRepository epics, IProjectAccessPolicy access)
    {
        _tasks = tasks;
        _audit = audit;
        _realtime = realtime;
        _epics = epics;
        _access = access;
    }

    public async Task<ServiceResult<TaskDto>> Handle(PauseTaskCommand cmd, CancellationToken ct)
    {
        var task = await _tasks.GetByIdAsync(cmd.TaskId, ct);
        if (task is null)
            return ServiceResult<TaskDto>.Fail("NOT_FOUND", $"Task '{cmd.TaskId}' not found.");

        if (task.AssigneeId != cmd.ActorId
            && !await _access.CanAccessProjectAsync(task.ProjectId, cmd.ActorId, cmd.ActorRole, ct))
            return ServiceResult<TaskDto>.Fail("FORBIDDEN", "You do not have access to this task.");

        try
        {
            task.Pause(cmd.Note, cmd.ActorId);
        }
        catch (DomainException ex)
        {
            return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION", ex.Message);
        }

        await _tasks.SaveChangesAsync(ct);
        await EpicStatusRollUp.ApplyAsync(task.EpicId, _tasks, _epics, ct);

        await _audit.LogAsync("TASK_PAUSED", cmd.ActorId, cmd.IpAddress,
            $"Task {task.Id} paused" + (cmd.Note is null ? "." : $": {cmd.Note}"), ct);

        var dto = TaskDto.From(task);
        await _realtime.SendTaskUpdatedAsync(cmd.ActorId, dto, ct);

        return ServiceResult<TaskDto>.Ok(dto);
    }
}
