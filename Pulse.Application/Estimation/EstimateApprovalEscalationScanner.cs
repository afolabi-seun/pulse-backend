using System.Text.Json;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Notifications;
using Pulse.Application.Overwork;
using Pulse.Domain.Engineers;
using Pulse.Domain.Notifications;

namespace Pulse.Application.Estimation;

/// <summary>Widens approval of a pending Planning Poker estimate, one tier at a time, while nobody acts on it. It starts with the lead of the team
/// that owns the task; once a working hour has passed the owning department's head(s) are added; a working hour after that the PMO is added too.
/// Each step adds people and takes nobody away — a backstop, not a handoff. The clock counts working hours only (Monday to Friday, see
/// <see cref="WorkingHours"/>), so a request made on a Friday evening does not page anyone over the weekend. Runs on the same cadence as the
/// due-date EscalationScanner. See docs/planning-poker-approval-tiers-spec.md.</summary>
public class EstimateApprovalEscalationScanner : IRecurringJob
{
    private readonly IEstimationRepository _estimation;
    private readonly ITaskRepository _tasks;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;
    private readonly IProjectRepository _projects;
    private readonly INotificationDispatcher _notify;
    private readonly IAppSettings _settings;
    private readonly OverworkThresholds _thresholds;
    private readonly TimeProvider _clock;

    public EstimateApprovalEscalationScanner(
        IEstimationRepository estimation, ITaskRepository tasks, IEngineerRepository engineers, ITeamRepository teams, IProjectRepository projects,
        IAppSettings settings, OverworkThresholds thresholds, INotificationDispatcher notify, TimeProvider? clock = null)
    {
        _estimation = estimation;
        _tasks = tasks;
        _engineers = engineers;
        _teams = teams;
        _projects = projects;
        _notify = notify;
        _settings = settings;
        _thresholds = thresholds;
        // The real clock unless a test supplies its own: what is due depends on the time of day and the weekday.
        _clock = clock ?? TimeProvider.System;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        foreach (var session in await _estimation.ListPendingApprovalsAsync(ct))
            await ProcessAsync(session, now, ct);
    }

    private TimeSpan WorkedBetween(DateTime from, DateTime to) =>
        WorkingHours.Between(from, to, _thresholds.WorkdayStartHourUtc, _thresholds.WorkdayEndHourUtc);

    private async Task ProcessAsync(Domain.Tasks.TaskEstimationSession session, DateTime now, CancellationToken ct)
    {
        var task = await _tasks.GetByIdAsync(session.TaskId, ct);
        if (task is null || session.SubmittedAt is not DateTime submittedAt) return;

        var current = await EstimationApproval.ResolveApproversAsync(
            task.ProjectId, task.AssigneeId, EstimationApproval.ReachedBy(session), session.SubmittedBy, _projects, _engineers, _teams, ct);

        // Tier 2: the owning department's head(s), once the team lead has had a working hour. Only meaningful while the request still sits at the team
        // lead — one that already resolves to the head (no lead to ask) is past this tier from the start.
        if (!session.EscalatedToHead && current.Stage == ApprovalStage.TeamLead)
        {
            if (WorkedBetween(submittedAt, now) < TimeSpan.FromHours(_thresholds.EstimateApprovalEscalationHours)) return;

            session.EscalateToHead(now);
            await _estimation.SaveChangesAsync(ct);

            var withHeads = await EstimationApproval.ResolveApproversAsync(
                task.ProjectId, task.AssigneeId, ApprovalStage.DepartmentHead, session.SubmittedBy, _projects, _engineers, _teams, ct);
            await NotifyAddedAsync(task, session, current, withHeads, ct);
            return;
        }

        // Tier 3: the PMO, a working hour after the heads were added (or after submission, when it started at the head).
        if (current.Stage == ApprovalStage.Pmo) return; // already includes the PMO from the start
        var headsSince = session.EscalatedToHeadAt ?? submittedAt;
        if (WorkedBetween(headsSince, now) < TimeSpan.FromHours(_thresholds.EstimateApprovalPmoEscalationHours)) return;

        session.EscalateToPmo();
        await _estimation.SaveChangesAsync(ct);

        var withPmo = await EstimationApproval.ResolveApproversAsync(
            task.ProjectId, task.AssigneeId, ApprovalStage.Pmo, session.SubmittedBy, _projects, _engineers, _teams, ct);
        await NotifyAddedAsync(task, session, current, withPmo, ct);
    }

    /// <summary>Only the newly added people get notified — whoever could already act already knows (they are the ones who did not act yet), and
    /// pinging them again would read as a nag rather than new information.</summary>
    private async Task NotifyAddedAsync(Domain.Tasks.PulseTask task, Domain.Tasks.TaskEstimationSession session,
        ApprovalState before, ApprovalState after, CancellationToken ct)
    {
        var alreadyAsked = before.Approvers.Select(a => a.Id).ToHashSet();
        var newlyAdded = after.Approvers.Where(a => !alreadyAsked.Contains(a.Id)).ToList();
        if (newlyAdded.Count == 0) return;

        var assignee = task.AssigneeId.HasValue ? await _engineers.GetByIdAsync(task.AssigneeId.Value, ct) : null;
        var waitingOn = before.Approvers.Count > 0 ? string.Join(", ", before.Approvers.Select(a => a.Name)) : null;
        var taskLink = $"{_settings.AppBaseUrl}/tasks/{task.Id}";

        foreach (var added in newlyAdded)
        {
            var isPmo = added.Role is Roles.HeadOfPmo or Roles.ProjectManager;
            var payload = JsonSerializer.Serialize(new
            {
                taskId = task.Id,
                taskTitle = task.Title,
                points = session.PendingApprovalPoints,
                assigneeName = assignee?.Name,
                teamLeadName = waitingOn,
                tier = isPmo ? "pmo" : "department_head",
            });

            await _notify.NotifyAsync(added.Id, NotificationKind.EstimateApprovalEscalated, payload, ct: ct);

            var waited = waitingOn is null ? "has been waiting for approval" : $"has been awaiting <strong>{waitingOn}</strong>'s approval";
            var why = isPmo
                ? "This notification was sent because you are part of the PMO, and neither the team lead nor the department head has acted."
                : "This notification was sent because you head the department that owns this task.";
            var body = $"""
                <p>Hi {added.Name},</p>
                <p>An estimate of <strong>{session.PendingApprovalPoints} pts</strong> for
                <strong>{task.Title}</strong> (assigned to {assignee?.Name ?? "an engineer"}) {waited} for over an hour of working time and now
                also needs your attention.</p>
                {EmailTemplate.Button(taskLink, "Review estimate")}
                {EmailTemplate.Muted(why)}
                """;
            await _notify.EmailAsync(added.Id, NotificationKind.EstimateApprovalEscalated, new NotificationEmail(added.Email, $"Estimate approval overdue: {task.Title}", EmailTemplate.Layout(body)), ct);
        }
    }
}
