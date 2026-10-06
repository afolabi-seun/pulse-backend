using System.Text.Json;
using Pulse.Application.Auth;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Notifications;
using Pulse.Domain.Notifications;
using MediatR;

namespace Pulse.Application.Estimation.Commands;

public record RejectEstimateCommand(Guid TaskId, string? Reason, Guid ActorId, string ActorRole) : IRequest<ServiceResult<Unit>>;

public class RejectEstimateHandler : IRequestHandler<RejectEstimateCommand, ServiceResult<Unit>>
{
    private readonly IEstimationRepository _estimation;
    private readonly ITaskRepository _tasks;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;
    private readonly INotificationDispatcher _notify;
    private readonly IAppSettings _settings;

    public RejectEstimateHandler(
        IEstimationRepository estimation,
        ITaskRepository tasks,
        IEngineerRepository engineers,
        ITeamRepository teams,
        IAppSettings settings,
        INotificationDispatcher notify)
    {
        _estimation = estimation;
        _tasks = tasks;
        _engineers = engineers;
        _teams = teams;
        _notify = notify;
        _settings = settings;
    }

    public async Task<ServiceResult<Unit>> Handle(RejectEstimateCommand request, CancellationToken ct)
    {
        var session = await _estimation.GetSessionAsync(request.TaskId, ct);
        if (session?.PendingApprovalPoints is not int points)
            return ServiceResult<Unit>.Fail("NOT_PENDING", "No estimate is currently pending approval for this task.");

        var task = await _tasks.GetByIdAsync(request.TaskId, ct);
        if (task is null)
            return ServiceResult<Unit>.Fail("NOT_FOUND", "Task not found.");

        if (request.ActorId == session.SubmittedBy)
            return ServiceResult<Unit>.Fail("FORBIDDEN", "You submitted this estimate, so someone else has to reject it.");

        if (!await EstimationApproval.IsAuthorizedAsync(task.AssigneeId, request.ActorId, request.ActorRole, session.EscalatedToHead, session.SubmittedBy, _engineers, _teams, ct))
            return ServiceResult<Unit>.Fail("FORBIDDEN", "Only the assignee's department head (or a Team Lead, if none is set up) can reject this estimate.");

        var submittedByBeforeClear = session.SubmittedBy;

        // Leaves votes and the reveal intact — only the pending request itself is cleared — so the
        // team can discuss and resubmit a different number without re-voting from scratch. A full
        // wipe (clearing votes too) is still available via the existing Reset action.
        session.ClearApprovalRequest();
        await _estimation.SaveChangesAsync(ct);

        if (submittedByBeforeClear is Guid submittedBy)
        {
            var rejector = await _engineers.GetByIdAsync(request.ActorId, ct);
            var payload = JsonSerializer.Serialize(new
            {
                taskId = task.Id, taskTitle = task.Title, points,
                rejectedByName = rejector?.Name, reason = request.Reason,
            });
            await _notify.NotifyAsync(submittedBy, NotificationKind.EstimateRejected, payload, ct: ct);

            var submitter = await _engineers.GetByIdAsync(submittedBy, ct);
            if (submitter is not null)
            {
                var taskLink = $"{_settings.AppBaseUrl}/tasks/{task.Id}";
                var reasonHtml = string.IsNullOrWhiteSpace(request.Reason) ? string.Empty : $"""
                    <p style="background:#fef2f2;border-left:4px solid #ef4444;padding:12px 16px;border-radius:4px;color:#991b1b;">
                      <strong>Reason:</strong> {System.Net.WebUtility.HtmlEncode(request.Reason)}
                    </p>
                    """;
                var body = $"""
                    <p>Hi {submitter.Name},</p>
                    <p><strong>{rejector?.Name ?? "The department head"}</strong> rejected the estimate of
                    <strong>{points} pts</strong> for <strong>{task.Title}</strong>.</p>
                    {reasonHtml}
                    {EmailTemplate.Button(taskLink, "View task")}
                    """;
                await _notify.EmailAsync(submitter.Id, NotificationKind.EstimateRejected, new NotificationEmail(submitter.Email, $"Estimate rejected: {task.Title}", EmailTemplate.Layout(body)), ct);
            }
        }

        return ServiceResult<Unit>.Ok(Unit.Value);
    }
}
