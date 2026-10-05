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

/// <summary>QA flags a problem with the review — the task stays in QA and nothing about its
/// status or timeline changes until <see cref="ConfirmQaRejectionCommand"/> or
/// <see cref="WithdrawQaRejectionCommand"/>. Gives the assignee a chance to respond first (e.g.
/// "this is an environment issue, not the implementation") before it's confirmed as a real
/// iteration — see <see cref="RespondToQaRejectionCommand"/>.</summary>
public record ProposeQaRejectionCommand(
    Guid QaTaskId,
    string Reason,
    Guid ActorId,
    string? IpAddress,
    string ActorRole = "",
    TaskStage? TargetStage = null) : IRequest<ServiceResult<TaskDto>>;

public class ProposeQaRejectionHandler : IRequestHandler<ProposeQaRejectionCommand, ServiceResult<TaskDto>>
{
    private readonly ITaskRepository _tasks;
    private readonly IAuditLogRepository _audit;
    private readonly INotificationRepository _notifications;
    private readonly IRealtimeNotifier _realtime;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;
    private readonly IEmailQueue _emailQueue;
    private readonly IAppSettings _settings;

    public ProposeQaRejectionHandler(
        ITaskRepository tasks,
        IAuditLogRepository audit,
        INotificationRepository notifications,
        IRealtimeNotifier realtime,
        IEngineerRepository engineers,
        ITeamRepository teams,
        IEmailQueue emailQueue,
        IAppSettings settings)
    {
        _tasks = tasks;
        _audit = audit;
        _notifications = notifications;
        _realtime = realtime;
        _engineers = engineers;
        _teams = teams;
        _emailQueue = emailQueue;
        _settings = settings;
    }

    public async Task<ServiceResult<TaskDto>> Handle(ProposeQaRejectionCommand cmd, CancellationToken ct)
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

        if (parentTask.Status != Domain.Tasks.TaskStatus.InQa)
            return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION",
                "Original task is not currently awaiting QA.");

        try
        {
            parentTask.ProposeQaRejection(cmd.Reason, cmd.ActorId, cmd.TargetStage);
        }
        catch (DomainException ex)
        {
            return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION", ex.Message);
        }

        await _tasks.SaveChangesAsync(ct);

        var proposer = await _engineers.GetByIdAsync(cmd.ActorId, ct);

        await _audit.LogAsync("QA_REJECTION_PROPOSED", cmd.ActorId, cmd.IpAddress,
            $"QA rejection proposed for task '{parentTask.Id}'. Reason: {cmd.Reason}", ct);

        if (parentTask.AssigneeId is Guid assigneeId)
        {
            var payload = JsonSerializer.Serialize(new
            {
                taskId     = parentTask.Id,
                taskTitle  = parentTask.Title,
                reason     = cmd.Reason,
                proposedBy = proposer?.Name,
            });

            var n = Notification.Create(assigneeId, NotificationKind.QaRejectionProposed, payload, NotificationChannel.InApp);
            await _notifications.AddAsync(n, ct);
            await _notifications.SaveChangesAsync(ct);
            await _realtime.SendNotificationAsync(assigneeId, NotificationDto.From(n), ct);

            var engineer = await _engineers.GetByIdAsync(assigneeId, ct);
            if (engineer is not null)
            {
                var taskLink = $"{_settings.AppBaseUrl}/tasks/{parentTask.Id}";
                var reviewedBy = proposer is not null ? $" by <strong>{proposer.Name}</strong>" : string.Empty;
                var body = $"""
                    <p>Hi {engineer.Name},</p>
                    <p>QA has raised a concern{reviewedBy} on <strong>{parentTask.Title}</strong> — it's still with QA, not back on your board yet.</p>
                    <p style="background:#fffbeb;border-left:4px solid #f59e0b;padding:12px 16px;border-radius:4px;color:#92400e;">
                      <strong>Concern:</strong> {System.Net.WebUtility.HtmlEncode(cmd.Reason)}
                    </p>
                    <p>If this turns out to be an environment or configuration issue rather than the implementation, respond below before QA confirms it.</p>
                    {EmailTemplate.Button(taskLink, "Respond")}
                    {EmailTemplate.Muted("This notification was sent because you are the assignee of this task.")}
                    """;
                _emailQueue.Enqueue(engineer.Email, $"QA raised a concern: {parentTask.Title}", EmailTemplate.Layout(body));
            }
        }

        return ServiceResult<TaskDto>.Ok(TaskDto.From(parentTask, pendingRejectionActorName: proposer?.Name));
    }
}
