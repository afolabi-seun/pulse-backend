using System.Text.Json;
using Pulse.Application.Auth;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Notifications;
using Pulse.Domain.Common;
using Pulse.Domain.Notifications;
using MediatR;

namespace Pulse.Application.Estimation.Commands;

/// <summary>The task's own assignee, or a Team Lead+ on their behalf, submits the revealed estimate
/// for tiered approval (see EstimationApproval) instead of writing points straight to the task.
/// See ApproveEstimateCommand/RejectEstimateCommand for the other half.</summary>
public record SubmitEstimateForApprovalCommand(Guid TaskId, int Points, Guid ActorId, string ActorRole) : IRequest<ServiceResult<Unit>>;

public class SubmitEstimateForApprovalHandler : IRequestHandler<SubmitEstimateForApprovalCommand, ServiceResult<Unit>>
{
    private static readonly int[] ValidPoints = [1, 2, 3, 5, 8, 13, 21];
    private readonly IEstimationRepository _estimation;
    private readonly ITaskRepository _tasks;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;
    private readonly INotificationRepository _notifications;
    private readonly IRealtimeNotifier _realtime;
    private readonly IEmailQueue _emailQueue;
    private readonly IAppSettings _settings;

    public SubmitEstimateForApprovalHandler(
        IEstimationRepository estimation, ITaskRepository tasks, IEngineerRepository engineers, ITeamRepository teams,
        INotificationRepository notifications, IRealtimeNotifier realtime, IEmailQueue emailQueue, IAppSettings settings)
    {
        _estimation = estimation;
        _tasks = tasks;
        _engineers = engineers;
        _teams = teams;
        _notifications = notifications;
        _realtime = realtime;
        _emailQueue = emailQueue;
        _settings = settings;
    }

    public async Task<ServiceResult<Unit>> Handle(SubmitEstimateForApprovalCommand request, CancellationToken ct)
    {
        if (!ValidPoints.Contains(request.Points))
            return ServiceResult<Unit>.Fail("INVALID_POINTS", $"Points must be one of: {string.Join(", ", ValidPoints)}.");

        var session = await _estimation.GetSessionAsync(request.TaskId, ct);
        if (session is null || !session.IsRevealed)
            return ServiceResult<Unit>.Fail("NOT_REVEALED", "Cards must be revealed before submitting an estimate.");

        var task = await _tasks.GetByIdAsync(request.TaskId, ct);
        if (task is null)
            return ServiceResult<Unit>.Fail("NOT_FOUND", "Task not found.");

        var isTeamLeadOrAbove = CapabilityRegistry.All[CapabilityRegistry.TeamLeadOrAbove].AllowedRoles.Contains(request.ActorRole);
        if (!isTeamLeadOrAbove && task.AssigneeId != request.ActorId)
            return ServiceResult<Unit>.Fail("FORBIDDEN", "Only the task's assignee, or a Team Lead and above, can submit an estimate for approval.");

        // Every approved estimate is a real, non-zero point value (see ValidPoints) — same rule
        // as CreateTaskCommand: a task can't carry real points without a due date to schedule
        // them against.
        if (task.DueDate is null)
            return ServiceResult<Unit>.Fail("BUSINESS_RULE_VIOLATION",
                "A due date is required before submitting a point estimate. Set one from the Edit form first.");

        try
        {
            session.SubmitForApproval(request.Points, request.ActorId);
        }
        catch (DomainException ex)
        {
            return ServiceResult<Unit>.Fail("BUSINESS_RULE_VIOLATION", ex.Message);
        }

        await _estimation.SaveChangesAsync(ct);

        await NotifyApproversAsync(task, request, ct);

        return ServiceResult<Unit>.Ok(Unit.Value);
    }

    private async Task NotifyApproversAsync(Domain.Tasks.PulseTask task, SubmitEstimateForApprovalCommand request, CancellationToken ct)
    {
        // A fresh submission is never pre-escalated (SubmitForApproval resets the flag) — always
        // resolves to whoever the first tier actually is (Team Lead, or straight to the head(s)
        // per EstimationApproval's own rules).
        var state = await EstimationApproval.ResolveApproversAsync(task.AssigneeId, escalatedToHead: false, request.ActorId, _engineers, _teams, ct);
        // No one resolvable at all (no assignee, or assignee has neither a Team Lead nor a
        // department head) — ApproveEstimateCommand falls back to allowing Team Lead+ in that
        // case, so there's no one specific to notify here beyond what the existing task-update
        // realtime push already covers.
        if (state is null || state.Approvers.Count == 0) return;

        var submitter = await _engineers.GetByIdAsync(request.ActorId, ct);
        var assignee = task.AssigneeId.HasValue ? await _engineers.GetByIdAsync(task.AssigneeId.Value, ct) : null;
        var taskLink = $"{_settings.AppBaseUrl}/tasks/{task.Id}";
        var roleLabel = state.Stage == ApprovalStage.TeamLead ? "as their team lead" : "as their department head";

        foreach (var approver in state.Approvers)
        {
            var payload = JsonSerializer.Serialize(new
            {
                taskId = task.Id,
                taskTitle = task.Title,
                points = request.Points,
                assigneeName = assignee?.Name,
                submittedByName = submitter?.Name,
            });

            var n = Notification.Create(approver.Id, NotificationKind.EstimateApprovalRequested, payload, NotificationChannel.InApp);
            await _notifications.AddAsync(n, ct);
            await _notifications.SaveChangesAsync(ct);
            await _realtime.SendNotificationAsync(approver.Id, NotificationDto.From(n), ct);

            var body = $"""
                <p>Hi {approver.Name},</p>
                <p><strong>{submitter?.Name ?? "A team lead"}</strong> submitted a Planning Poker estimate of
                <strong>{request.Points} pts</strong> for <strong>{task.Title}</strong>
                (assigned to {assignee?.Name ?? "an engineer"}) and it needs your approval.</p>
                {EmailTemplate.Button(taskLink, "Review estimate")}
                {EmailTemplate.Muted($"This notification was sent because you are {roleLabel} for this task's assignee.")}
                """;
            _emailQueue.Enqueue(approver.Email, $"Estimate approval needed: {task.Title}", EmailTemplate.Layout(body));
        }
    }
}
