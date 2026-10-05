using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Common;
using Pulse.Domain.TimeEntries;
using MediatR;

namespace Pulse.Application.TimeEntries.Commands;

public record StartTimerCommand(
    Guid EngineerId,
    string ActorRole,
    TimeEntryCategory Category,
    Guid? TaskId,
    int? Points,
    Guid ActorId,
    string? IpAddress,
    Guid? SubtaskId = null) : IRequest<ServiceResult<ActiveTimerDto>>;

public class StartTimerHandler : IRequestHandler<StartTimerCommand, ServiceResult<ActiveTimerDto>>
{
    private readonly IActiveTimerRepository _timers;
    private readonly ITimeEntryRepository _timeEntries;
    private readonly ITaskRepository _tasks;
    private readonly ISubtaskRepository _subtasks;
    private readonly IProjectAccessPolicy _access;
    private readonly IAuditLogRepository _audit;

    public StartTimerHandler(
        IActiveTimerRepository timers, ITimeEntryRepository timeEntries, ITaskRepository tasks,
        ISubtaskRepository subtasks, IProjectAccessPolicy access, IAuditLogRepository audit)
    {
        _timers = timers;
        _timeEntries = timeEntries;
        _tasks = tasks;
        _subtasks = subtasks;
        _access = access;
        _audit = audit;
    }

    public async Task<ServiceResult<ActiveTimerDto>> Handle(StartTimerCommand cmd, CancellationToken ct)
    {
        string? taskTitle = null;
        string? subtaskTitle = null;

        if (cmd.SubtaskId.HasValue && cmd.Category != TimeEntryCategory.Task)
            return ServiceResult<ActiveTimerDto>.Fail("VALIDATION_ERROR", "A subtask timer must be a Task-category timer.");

        if (cmd.Category == TimeEntryCategory.Task)
        {
            if (!cmd.TaskId.HasValue)
                return ServiceResult<ActiveTimerDto>.Fail("VALIDATION_ERROR", "TaskId is required to start a task timer.");

            var task = await _tasks.GetByIdAsync(cmd.TaskId.Value, ct);
            if (task is null)
                return ServiceResult<ActiveTimerDto>.Fail("NOT_FOUND", $"Task '{cmd.TaskId}' not found.");

            if (!await _access.CanAccessTaskAsync(cmd.TaskId.Value, cmd.ActorId, cmd.ActorRole, ct))
                return ServiceResult<ActiveTimerDto>.Fail("FORBIDDEN", "You do not have access to this task.");

            Domain.Tasks.Subtask? subtask = null;
            if (cmd.SubtaskId.HasValue)
            {
                subtask = await _subtasks.GetByIdAsync(cmd.SubtaskId.Value, ct);
                if (subtask is null || subtask.TaskId != cmd.TaskId.Value)
                    return ServiceResult<ActiveTimerDto>.Fail("NOT_FOUND", $"Subtask '{cmd.SubtaskId}' not found on this task.");
            }

            // A subtask explicitly loaned to this engineer overrides the parent task's own
            // assignee — that's the whole point of loaning a checklist item to someone else.
            // Everything else (an un-loaned subtask, or no subtask at all) falls back to the
            // task-level ownership rule below.
            var loanedToMe = subtask?.AssigneeId == cmd.EngineerId;
            if (subtask is not null && subtask.AssigneeId is { } subtaskAssignee && subtaskAssignee != cmd.EngineerId)
                return ServiceResult<ActiveTimerDto>.Fail("FORBIDDEN", "This subtask is loaned to someone else.");

            if (!loanedToMe)
            {
                if (task.AssigneeId is { } assigneeId && assigneeId != cmd.EngineerId)
                    return ServiceResult<ActiveTimerDto>.Fail("FORBIDDEN", "This task is already assigned to someone else.");

                if (task.AssigneeId is null)
                    task.Assign(cmd.EngineerId, cmd.ActorId);
            }

            if (cmd.Points.HasValue)
                task.SetPoints(cmd.Points.Value, cmd.ActorId);

            try
            {
                task.ActivateForTimer(cmd.ActorId);
            }
            catch (DomainException ex)
            {
                return ServiceResult<ActiveTimerDto>.Fail("BUSINESS_RULE_VIOLATION", ex.Message);
            }

            await _tasks.SaveChangesAsync(ct);
            taskTitle = task.Title;
            subtaskTitle = subtask?.Title;
        }

        var existing = await _timers.GetByEngineerAsync(cmd.EngineerId, ct);
        if (existing is not null)
            await StopAndLog(existing, cmd.ActorId, cmd.IpAddress, ct);

        ActiveTimer timer;
        try
        {
            timer = ActiveTimer.Start(cmd.EngineerId, cmd.Category, cmd.TaskId, cmd.SubtaskId);
        }
        catch (DomainException ex)
        {
            return ServiceResult<ActiveTimerDto>.Fail("BUSINESS_RULE_VIOLATION", ex.Message);
        }

        await _timers.AddAsync(timer, ct);
        await _timers.SaveChangesAsync(ct);

        await _audit.LogAsync("TIME_ENTRY_TIMER_STARTED", cmd.ActorId, cmd.IpAddress,
            $"Engineer {cmd.EngineerId} started a {cmd.Category} timer" + (cmd.TaskId.HasValue ? $" on task {cmd.TaskId}" : string.Empty), ct);

        return ServiceResult<ActiveTimerDto>.Ok(ActiveTimerDto.From(timer, taskTitle, subtaskTitle));
    }

    private async Task StopAndLog(ActiveTimer timer, Guid actorId, string? ipAddress, CancellationToken ct)
    {
        // Floor at 0.01h: TimeEntry.Hours must be >0, and a timer replaced moments after it started
        // (e.g. starting a new one right after) could otherwise round to exactly 0.
        var hours = Math.Clamp(Math.Round((decimal)(DateTime.UtcNow - timer.StartedAt).TotalHours, 2), 0.01m, 24m);
        var entry = TimeEntry.Log(
            timer.EngineerId, DateOnly.FromDateTime(timer.StartedAt), timer.Category, timer.TaskId, null, hours, null, timer.SubtaskId);

        await _timeEntries.AddAsync(entry, ct);
        await _timers.DeleteAsync(timer, ct);
        await _timeEntries.SaveChangesAsync(ct);

        await _audit.LogAsync("TIME_ENTRY_TIMER_STOPPED", actorId, ipAddress,
            $"Engineer {timer.EngineerId} auto-stopped a running timer ({hours}h) to start a new one", ct);
    }
}
