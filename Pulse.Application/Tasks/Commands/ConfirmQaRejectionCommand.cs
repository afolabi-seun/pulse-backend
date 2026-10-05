using System.Text.Json;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Notifications;
using Pulse.Domain.Common;
using Pulse.Domain.Engineers;
using Pulse.Domain.Notifications;
using Pulse.Domain.Tasks;
using MediatR;

namespace Pulse.Application.Tasks.Commands;

/// <summary>QA is satisfied the rejection should stand (whether or not the assignee responded) —
/// this is the actual "sent back for rework" effect, moved here from ProposeQaRejectionCommand so
/// it only fires once QA has had a chance to weigh a response.</summary>
public record ConfirmQaRejectionCommand(
    Guid QaTaskId,
    Guid ActorId,
    string? IpAddress,
    string ActorRole = "") : IRequest<ServiceResult<TaskDto>>;

public class ConfirmQaRejectionHandler : IRequestHandler<ConfirmQaRejectionCommand, ServiceResult<TaskDto>>
{
    private readonly ITaskRepository _tasks;
    private readonly IAuditLogRepository _audit;
    private readonly INotificationRepository _notifications;
    private readonly IRealtimeNotifier _realtime;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;
    private readonly IProjectRepository _projects;
    private readonly IEmailQueue _emailQueue;
    private readonly IAppSettings _settings;

    public ConfirmQaRejectionHandler(
        ITaskRepository tasks,
        IAuditLogRepository audit,
        INotificationRepository notifications,
        IRealtimeNotifier realtime,
        IEngineerRepository engineers,
        ITeamRepository teams,
        IProjectRepository projects,
        IEmailQueue emailQueue,
        IAppSettings settings)
    {
        _tasks = tasks;
        _audit = audit;
        _notifications = notifications;
        _realtime = realtime;
        _engineers = engineers;
        _teams = teams;
        _projects = projects;
        _emailQueue = emailQueue;
        _settings = settings;
    }

    public async Task<ServiceResult<TaskDto>> Handle(ConfirmQaRejectionCommand cmd, CancellationToken ct)
    {
        var qaTask = await _tasks.GetByIdAsync(cmd.QaTaskId, ct);
        if (qaTask is null)
            return ServiceResult<TaskDto>.Fail("NOT_FOUND", $"QA task '{cmd.QaTaskId}' not found.");

        if (!await QaRejectionPolicy.CanRejectAsync(qaTask, cmd.ActorId, cmd.ActorRole, _engineers, _teams, ct))
            return ServiceResult<TaskDto>.Fail("FORBIDDEN", "You do not have access to this task.");

        if (!qaTask.ParentTaskId.HasValue)
            return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION",
                "This task is not a QA task — it has no parent task.");

        var parentTask = await _tasks.GetByIdAsync(qaTask.ParentTaskId.Value, ct);
        if (parentTask is null)
            return ServiceResult<TaskDto>.Fail("NOT_FOUND", "Original task not found.");

        var reason = parentTask.PendingRejectionReason;
        var originalAssigneeId = parentTask.AssigneeId;
        // QA flagged this as backend's issue while the task is currently with Frontend — the
        // reverse direction (flagged Frontend while at Backend) has no stored frontend assignee to
        // auto-route to (see PendingRejectionTargetStage's own doc comment), so that direction
        // stays a manual "Hand off to Frontend" click.
        var shouldAutoRoute = parentTask.RequiresFrontendHandoff
            && parentTask.PendingRejectionTargetStage == TaskStage.Backend
            && parentTask.CurrentStage == TaskStage.Frontend
            && parentTask.BackendAssigneeId.HasValue;

        Engineer? routedTo = null;

        try
        {
            parentTask.ConfirmQaRejection(cmd.ActorId);
            // Close the QA task now that the rejection is final — matches the original
            // RejectQaCommand's effect, just triggered at confirm time instead of propose time.
            parentTask.SetQaTaskId(null);
            qaTask.MarkDone(cmd.ActorId);

            if (shouldAutoRoute)
            {
                var backendEngineer = await _engineers.GetByIdAsync(parentTask.BackendAssigneeId!.Value, ct);
                // An inactive/wrong-discipline stored backend engineer just means this stays
                // manual — not a reason to fail an otherwise-valid QA rejection confirm.
                if (backendEngineer is { IsActive: true, Discipline: Discipline.Backend })
                {
                    parentTask.HandOffToBackend(backendEngineer.Id, cmd.ActorId);
                    routedTo = backendEngineer;
                }
            }
        }
        catch (DomainException ex)
        {
            return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION", ex.Message);
        }

        await _tasks.SaveChangesAsync(ct);

        var confirmer = await _engineers.GetByIdAsync(cmd.ActorId, ct);

        await _audit.LogAsync("QA_REJECTED", cmd.ActorId, cmd.IpAddress,
            $"QA rejection confirmed for task '{parentTask.Id}'. Reason: {reason}", ct);

        if (routedTo is not null)
        {
            // Handoff grants project access, matching HandOffToBackendCommand's own side effect —
            // without it, a cross-project auto-route would hand someone a task they can't open.
            await _projects.AddMemberAsync(parentTask.ProjectId, routedTo.Id, ct);
            await _audit.LogAsync("TASK_AUTO_HANDED_OFF_TO_BACKEND", cmd.ActorId, cmd.IpAddress,
                $"Task '{parentTask.Id}' auto-routed to backend engineer '{routedTo.Id}' after a QA rejection flagged as a backend issue.", ct);
        }

        if (originalAssigneeId is Guid assigneeId)
        {
            var payload = JsonSerializer.Serialize(new
            {
                taskId         = parentTask.Id,
                taskTitle      = parentTask.Title,
                reason         = reason,
                rejectedByName = confirmer?.Name,
                routedToName   = routedTo?.Name,
            });

            var n = Notification.Create(assigneeId, NotificationKind.QaRejected, payload, NotificationChannel.InApp);
            await _notifications.AddAsync(n, ct);
            await _notifications.SaveChangesAsync(ct);
            await _realtime.SendNotificationAsync(assigneeId, NotificationDto.From(n), ct);

            var engineer = await _engineers.GetByIdAsync(assigneeId, ct);
            if (engineer is not null)
            {
                var taskLink = $"{_settings.AppBaseUrl}/tasks/{parentTask.Id}";
                var reviewedBy = confirmer is not null ? $" by <strong>{confirmer.Name}</strong>" : string.Empty;
                var nextStepHtml = routedTo is not null
                    ? $"<p>QA flagged this as a backend issue, so it's been automatically routed to <strong>{routedTo.Name}</strong> to address.</p>"
                    : "<p>Please address the issues raised and re-submit when ready.</p>";
                var body = $"""
                    <p>Hi {engineer.Name},</p>
                    <p>Your work on <strong>{parentTask.Title}</strong> was reviewed{reviewedBy} and has been returned for rework.</p>
                    <p style="background:#fef2f2;border-left:4px solid #ef4444;padding:12px 16px;border-radius:4px;color:#991b1b;">
                      <strong>Reason:</strong> {System.Net.WebUtility.HtmlEncode(reason ?? "")}
                    </p>
                    {nextStepHtml}
                    {EmailTemplate.Button(taskLink, "View task")}
                    {EmailTemplate.Muted("This notification was sent because you are the assignee of this task.")}
                    """;
                _emailQueue.Enqueue(engineer.Email, $"QA rejected: {parentTask.Title}", EmailTemplate.Layout(body));
            }
        }

        if (routedTo is not null && routedTo.Id != cmd.ActorId)
        {
            var payload = JsonSerializer.Serialize(new { taskId = parentTask.Id, taskTitle = parentTask.Title });
            var n = Notification.Create(routedTo.Id, NotificationKind.TaskAssigned, payload, NotificationChannel.InApp);
            await _notifications.AddAsync(n, ct);
            await _notifications.SaveChangesAsync(ct);
            await _realtime.SendNotificationAsync(routedTo.Id, NotificationDto.From(n), ct);

            var taskLink = $"{_settings.AppBaseUrl}/tasks/{parentTask.Id}";
            var body = $"""
                <p>Hi {routedTo.Name},</p>
                <p>A task has been routed to you for backend work, after QA's rejection of <strong>{parentTask.Title}</strong> was flagged as a backend issue.</p>
                {(parentTask.DueDate.HasValue ? $"<p>Due date: <strong>{parentTask.DueDate.Value:D}</strong></p>" : "")}
                {EmailTemplate.Button(taskLink, "View task")}
                {EmailTemplate.Muted("This notification was sent because you were handed off this task's backend work.")}
                """;
            _emailQueue.Enqueue(routedTo.Email, $"Task routed to you: {parentTask.Title}", EmailTemplate.Layout(body));
        }

        return ServiceResult<TaskDto>.Ok(TaskDto.From(parentTask, reactivatedByName: confirmer?.Name));
    }
}
