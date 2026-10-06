using System.Text.Json;
using Pulse.Application.CheckIns;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Notifications;
using Pulse.Domain.Common;
using Pulse.Domain.Notifications;
using Pulse.Domain.Tasks;
using MediatR;

namespace Pulse.Application.Tasks.Commands;

/// <summary>The reverse of HandOffToFrontendCommand — sends a RequiresFrontendHandoff task back to a
/// backend developer, e.g. after picking the wrong frontend engineer or discovering more backend work
/// is needed. Same shape: the current (frontend) assignee, or anyone with project access, may call it;
/// the target must be an active engineer whose Discipline is Backend.</summary>
public record HandOffToBackendCommand(
    Guid TaskId,
    Guid BackendAssigneeId,
    Guid ActorId,
    string? IpAddress,
    string ActorRole = "") : IRequest<ServiceResult<TaskDto>>;

public class HandOffToBackendHandler : IRequestHandler<HandOffToBackendCommand, ServiceResult<TaskDto>>
{
    private readonly ITaskRepository _tasks;
    private readonly IEngineerRepository _engineers;
    private readonly IProjectRepository _projects;
    private readonly IAuditLogRepository _audit;
    private readonly IProjectAccessPolicy _access;
    private readonly IAppSettings _settings;
    private readonly ICheckInRepository _checkIns;
    private readonly INotificationDispatcher _notify;

    public HandOffToBackendHandler(
        ITaskRepository tasks,
        IEngineerRepository engineers,
        IProjectRepository projects,
        IAuditLogRepository audit,
        IProjectAccessPolicy access,
        IAppSettings settings,
        ICheckInRepository checkIns,
        INotificationDispatcher notify)
    {
        _notify = notify;
        _tasks = tasks;
        _engineers = engineers;
        _projects = projects;
        _audit = audit;
        _access = access;
        _settings = settings;
        _checkIns = checkIns;
    }

    public async Task<ServiceResult<TaskDto>> Handle(HandOffToBackendCommand cmd, CancellationToken ct)
    {
        var task = await _tasks.GetByIdAsync(cmd.TaskId, ct);
        if (task is null)
            return ServiceResult<TaskDto>.Fail("NOT_FOUND", $"Task '{cmd.TaskId}' not found.");

        if (task.AssigneeId != cmd.ActorId
            && !await _access.CanAccessProjectAsync(task.ProjectId, cmd.ActorId, cmd.ActorRole, ct))
            return ServiceResult<TaskDto>.Fail("FORBIDDEN", "You do not have access to this task.");

        var target = await _engineers.GetByIdAsync(cmd.BackendAssigneeId, ct);
        if (target is null || !target.IsActive)
            return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION", "Assignee not found or inactive.");
        if (target.Discipline != Discipline.Backend)
            return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION",
                "A backend handoff can only be assigned to an engineer whose discipline is Backend.");

        // Whose stage just ended: the engineer holding the task now (the actor, if it had no assignee).
        // PMO can perform a hand-off on someone's behalf, and the check-in entry belongs to the engineer
        // who did the work, not to whoever clicked the button.
        var handingOffEngineerId = task.AssigneeId ?? cmd.ActorId;

        try
        {
            task.HandOffToBackend(cmd.BackendAssigneeId, cmd.ActorId);
        }
        catch (DomainException ex)
        {
            return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION", ex.Message);
        }

        await _tasks.SaveChangesAsync(ct);

        // Handoff grants project access, matching AssignTaskCommand's own assignment path.
        await _projects.AddMemberAsync(task.ProjectId, cmd.BackendAssigneeId, ct);

        await AutoCheckIn.EnsureForTaskHandoffAsync(_checkIns, handingOffEngineerId, task.ProjectId, "Backend", task.Title, ct);

        await _audit.LogAsync("TASK_HANDED_OFF_TO_BACKEND", cmd.ActorId, cmd.IpAddress,
            $"Task '{task.Id}' handed off to backend engineer '{cmd.BackendAssigneeId}'", ct);

        if (cmd.BackendAssigneeId != cmd.ActorId)
        {
            await _notify.NotifyAsync(cmd.BackendAssigneeId, NotificationKind.TaskAssigned, JsonSerializer.Serialize(new { taskId = task.Id, taskTitle = task.Title }), ct: ct);

            var taskLink = $"{_settings.AppBaseUrl}/tasks/{task.Id}";
            var body = $"""
                <p>Hi {target.Name},</p>
                <p>A task has been handed back to you for backend work: <strong>{task.Title}</strong>.</p>
                {(task.DueDate.HasValue ? $"<p>Due date: <strong>{task.DueDate.Value:D}</strong></p>" : "")}
                {EmailTemplate.Button(taskLink, "View task")}
                {EmailTemplate.Muted("This notification was sent because you were handed off this task's backend work.")}
                """;
            await _notify.EmailAsync(target.Id, NotificationKind.TaskAssigned, new NotificationEmail(target.Email, $"Task handed off: {task.Title}", EmailTemplate.Layout(body)), ct);
        }

        return ServiceResult<TaskDto>.Ok(TaskDto.From(task));
    }
}
