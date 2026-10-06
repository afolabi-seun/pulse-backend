using System.Text.Json;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Notifications;
using Pulse.Application.Overwork;
using Pulse.Domain.Engineers;
using Pulse.Domain.Notifications;

namespace Pulse.Application.Estimation;

/// <summary>Finds pending Planning Poker estimate approvals whose Team Lead grace period has
/// elapsed and escalates them to also include the department head — a backstop, not a handoff:
/// the Team Lead keeps the ability to act afterward. Runs on the same pulse as the due-date
/// EscalationScanner. See docs/planning-poker-approval-tiers-spec.md.</summary>
public class EstimateApprovalEscalationScanner : IRecurringJob
{
    private readonly IEstimationRepository _estimation;
    private readonly ITaskRepository _tasks;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;
    private readonly INotificationDispatcher _notify;
    private readonly IAppSettings _settings;
    private readonly OverworkThresholds _thresholds;

    public EstimateApprovalEscalationScanner(
        IEstimationRepository estimation,
        ITaskRepository tasks,
        IEngineerRepository engineers,
        ITeamRepository teams,
        IAppSettings settings,
        OverworkThresholds thresholds,
        INotificationDispatcher notify)
    {
        _estimation = estimation;
        _tasks = tasks;
        _engineers = engineers;
        _teams = teams;
        _notify = notify;
        _settings = settings;
        _thresholds = thresholds;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddHours(-_thresholds.EstimateApprovalEscalationHours);
        var sessions = await _estimation.GetPendingApprovalsOlderThanAsync(cutoff, ct);

        foreach (var session in sessions)
            await ProcessAsync(session, ct);
    }

    private async Task ProcessAsync(Domain.Tasks.TaskEstimationSession session, CancellationToken ct)
    {
        var task = await _tasks.GetByIdAsync(session.TaskId, ct);
        if (task?.AssigneeId is null) return;

        // Confirm this session is genuinely still sitting at the Team Lead tier — a task
        // reassigned since submission (new assignee, no Team Lead of their own, etc.) could
        // already resolve straight to the department head, in which case there's nothing to
        // escalate to.
        var before = await EstimationApproval.ResolveApproversAsync(task.AssigneeId, escalatedToHead: false, session.SubmittedBy, _engineers, _teams, ct);
        if (before is not { Stage: ApprovalStage.TeamLead }) return;

        session.EscalateToHead();
        await _estimation.SaveChangesAsync(ct);

        var after = await EstimationApproval.ResolveApproversAsync(task.AssigneeId, escalatedToHead: true, session.SubmittedBy, _engineers, _teams, ct);
        if (after is null) return;

        // Only the newly-added department head(s) get notified here — the Team Lead already knows
        // (they're the one who didn't act yet); re-pinging them would read as a nag, not new info.
        var teamLeadIds = before.Approvers.Select(a => a.Id).ToHashSet();
        var newlyAdded = after.Approvers.Where(a => !teamLeadIds.Contains(a.Id)).ToList();
        if (newlyAdded.Count == 0) return;

        var teamLead = before.Approvers[0];
        var assignee = await _engineers.GetByIdAsync(task.AssigneeId.Value, ct);
        var taskLink = $"{_settings.AppBaseUrl}/tasks/{task.Id}";
        var hours = (int)_thresholds.EstimateApprovalEscalationHours;

        foreach (var head in newlyAdded)
        {
            var payload = JsonSerializer.Serialize(new
            {
                taskId = task.Id,
                taskTitle = task.Title,
                points = session.PendingApprovalPoints,
                assigneeName = assignee?.Name,
                teamLeadName = teamLead.Name,
            });

            await _notify.NotifyAsync(head.Id, NotificationKind.EstimateApprovalEscalated, payload, ct: ct);

            var body = $"""
                <p>Hi {head.Name},</p>
                <p>An estimate of <strong>{session.PendingApprovalPoints} pts</strong> for
                <strong>{task.Title}</strong> (assigned to {assignee?.Name ?? "an engineer"}) has been
                awaiting <strong>{teamLead.Name}</strong>'s approval for over {hours} hours and now
                also needs your attention.</p>
                {EmailTemplate.Button(taskLink, "Review estimate")}
                {EmailTemplate.Muted("This notification was sent because you are the head of this engineer's department.")}
                """;
            await _notify.EmailAsync(head.Id, NotificationKind.EstimateApprovalEscalated, new NotificationEmail(head.Email, $"Estimate approval overdue: {task.Title}", EmailTemplate.Layout(body)), ct);
        }
    }
}
