using Pulse.Application.CheckIns;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Common;
using MediatR;

namespace Pulse.Application.Tasks.Commands;

/// <summary>The assignee, or anyone with access to the task's project (team lead, PMO, department head), says how far along the task is: 0, 25, 50 or 75
/// percent. The points it earns follow from that, recorded when the task is saved (see PulseDbContext). A task with a checklist is as far along as its
/// checklist, so this is refused for it.</summary>
public record SetTaskProgressCommand(Guid TaskId, int Percent, Guid ActorId, string ActorRole, string? IpAddress) : IRequest<ServiceResult<TaskDto>>;

public class SetTaskProgressHandler : IRequestHandler<SetTaskProgressCommand, ServiceResult<TaskDto>>
{
    private readonly ITaskRepository _tasks;
    private readonly ISubtaskRepository _subtasks;
    private readonly IProjectAccessPolicy _access;
    private readonly IRealtimeNotifier _realtime;
    private readonly IAuditLogRepository _audit;
    private readonly ICheckInRepository _checkIns;

    public SetTaskProgressHandler(ITaskRepository tasks, ISubtaskRepository subtasks, IProjectAccessPolicy access, IRealtimeNotifier realtime, IAuditLogRepository audit, ICheckInRepository checkIns)
    {
        _checkIns = checkIns;
        _tasks = tasks;
        _subtasks = subtasks;
        _access = access;
        _realtime = realtime;
        _audit = audit;
    }

    public async Task<ServiceResult<TaskDto>> Handle(SetTaskProgressCommand cmd, CancellationToken ct)
    {
        var task = await _tasks.GetByIdAsync(cmd.TaskId, ct);
        if (task is null)
            return ServiceResult<TaskDto>.Fail("NOT_FOUND", $"Task '{cmd.TaskId}' not found.");

        if (task.AssigneeId != cmd.ActorId && !await _access.CanAccessProjectAsync(task.ProjectId, cmd.ActorId, cmd.ActorRole, ct))
            return ServiceResult<TaskDto>.Fail("FORBIDDEN", "You do not have access to this task.");

        var checklist = await _subtasks.GetByTaskIdAsync(cmd.TaskId, ct);
        if (checklist.Count > 0)
            return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION", "This task has a checklist, so its progress follows the checklist. Tick items off there.");

        var before = task.ProgressPercent;
        try
        {
            task.SetProgress(cmd.Percent, cmd.ActorId);
        }
        catch (DomainException ex)
        {
            return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION", ex.Message);
        }

        await _tasks.SaveChangesAsync(ct);
        // Moving one's own task forward counts as having checked in today; moving it back, or a lead setting someone else's, does not.
        if (cmd.Percent > before && task.AssigneeId == cmd.ActorId)
            await AutoCheckIn.EnsureForProgressAsync(_checkIns, cmd.ActorId, task.ProjectId, task.Title, cmd.Percent, ct);

        await _audit.LogAsync("TASK_PROGRESS_SET", cmd.ActorId, cmd.IpAddress, $"Task {task.Id} progress set to {cmd.Percent}%", ct);

        var dto = TaskDto.From(task);
        await _realtime.SendTaskUpdatedAsync(cmd.ActorId, dto, ct);
        return ServiceResult<TaskDto>.Ok(dto);
    }
}
