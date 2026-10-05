using System.Text.Json;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Notifications;
using Pulse.Application.Overwork;
using Pulse.Domain.Engineers;
using Pulse.Domain.Escalations;
using Pulse.Domain.Notifications;
using Pulse.Domain.Teams;

namespace Pulse.Application.Escalations;

public class EscalationScanner
{
    private readonly ITaskRepository _tasks;
    private readonly IEscalationEventRepository _events;
    private readonly IEmailQueue _emailQueue;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;
    private readonly INotificationRepository _notifications;
    private readonly IRealtimeNotifier _realtime;
    private readonly OverworkThresholds _thresholds;
    private readonly IProjectAccessPolicy _access;

    public EscalationScanner(
        ITaskRepository tasks,
        IEscalationEventRepository events,
        IEmailQueue emailQueue,
        IEngineerRepository engineers,
        ITeamRepository teams,
        INotificationRepository notifications,
        IRealtimeNotifier realtime,
        OverworkThresholds thresholds,
        IProjectAccessPolicy access)
    {
        _tasks = tasks;
        _events = events;
        _emailQueue = emailQueue;
        _engineers = engineers;
        _teams = teams;
        _notifications = notifications;
        _realtime = realtime;
        _thresholds = thresholds;
        _access = access;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var daysLookahead = (int)Math.Ceiling(_thresholds.EscalationT3Days);
        var candidates = await _tasks.GetEscalationCandidatesAsync(daysLookahead, ct);
        var isWeekend = DateTime.UtcNow.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        var now = DateTime.UtcNow;

        // Personal tasks (an HR/Accountant's own to-dos) are reminded about too, but only to their owner:
        // no team-lead copy, no PM/head overdue broadcast. They come from a separate query so the shared
        // candidate list the Escalations page and reports use never contains them.
        var personalCandidates = await _tasks.GetPersonalEscalationCandidatesAsync(daysLookahead, ct);

        foreach (var task in candidates)
            await ProcessAsync(task, ownerOnly: false, isWeekend, now, ct);
        foreach (var task in personalCandidates)
            await ProcessAsync(task, ownerOnly: true, isWeekend, now, ct);
    }

    private async Task ProcessAsync(Domain.Tasks.PulseTask task, bool ownerOnly, bool isWeekend, DateTime now, CancellationToken ct)
    {
        // Ownership has moved to QA, the engineer has voluntarily paused the task, or
        // there's no assignee yet (Backlog) — don't fire escalation notifications for any of these.
        if (task.Status is Domain.Tasks.TaskStatus.InQa or Domain.Tasks.TaskStatus.Paused or Domain.Tasks.TaskStatus.Backlog)
            return;

        // GetEscalationCandidatesAsync filters DueDate <= threshold, so DueDate is always set here.
        var level = EscalationLevelCalculator.Determine(task.DueDate!.Value, task.ActivatedAt, _thresholds, now);
        if (level is null) return;
        if (await _events.AlreadyFiredAsync(task.Id, level.Value, ct)) return;

        await _events.RecordAsync(task.Id, level.Value, ct);

        var payload = JsonSerializer.Serialize(new { taskId = task.Id, taskTitle = task.Title, dueDate = task.DueDate });

        if (level == EscalationLevel.TMinus3)
            await FireT3Async(task.Id, task.Title, task.DueDate!.Value, task.AssigneeId, payload, isWeekend, ownerOnly, ct);
        else if (level == EscalationLevel.TMinus1)
            await FireT1Async(task.Id, task.Title, task.DueDate!.Value, task.AssigneeId, payload, isWeekend, ownerOnly, ct);
        else
            await FireOverdueAsync(task.Id, task.ProjectId, task.Title, task.DueDate!.Value, task.AssigneeId, payload, isWeekend, task.ParentTaskId.HasValue, ownerOnly, ct);
    }

    private async Task FireT3Async(Guid taskId, string title, DateOnly dueDate,
        Guid? assigneeId, string payload, bool isWeekend, bool ownerOnly, CancellationToken ct)
    {
        if (assigneeId is null) return;

        var engineer = await _engineers.GetByIdAsync(assigneeId.Value, ct);
        if (engineer is null) return;

        var t3Notif = Notification.Create(engineer.Id, NotificationKind.EscalationT3, payload);
        await _notifications.AddAsync(t3Notif, ct);
        await _notifications.SaveChangesAsync(ct);
        await _realtime.SendNotificationAsync(engineer.Id, NotificationDto.From(t3Notif), ct);

        if (!isWeekend)
        {
            var engineerBody = $"""
                <p>Hi {engineer.Name},</p>
                <p>Your task <strong>{title}</strong> is due on <strong>{dueDate:D}</strong> — 3 days from now.</p>
                <p>Please ensure you're on track to complete it by the due date.</p>
                """;
            _emailQueue.Enqueue(engineer.Email, $"Task due in 3 days: {title}", EmailTemplate.Layout(engineerBody));

            var teamLead = ownerOnly ? null : await GetTeamLeadAsync(engineer, ct);
            if (teamLead is not null && teamLead.Id != engineer.Id)
            {
                var leadNotif = Notification.Create(teamLead.Id, NotificationKind.EscalationT3, payload);
                await _notifications.AddAsync(leadNotif, ct);
                await _notifications.SaveChangesAsync(ct);
                await _realtime.SendNotificationAsync(teamLead.Id, NotificationDto.From(leadNotif), ct);

                var leadBody = $"""
                    <p>Hi {teamLead.Name},</p>
                    <p><strong>{engineer.Name}</strong>'s task <strong>{title}</strong> is due on <strong>{dueDate:D}</strong> — 3 days from now.</p>
                    <p>Please check in with them to ensure they're on track.</p>
                    """;
                _emailQueue.Enqueue(teamLead.Email, $"Team task due in 3 days: {title}", EmailTemplate.Layout(leadBody));
            }
        }
    }

    private async Task FireT1Async(Guid taskId, string title, DateOnly dueDate,
        Guid? assigneeId, string payload, bool isWeekend, bool ownerOnly, CancellationToken ct)
    {
        if (assigneeId is null) return;

        var engineer = await _engineers.GetByIdAsync(assigneeId.Value, ct);
        if (engineer is null) return;

        var t1Notif = Notification.Create(engineer.Id, NotificationKind.EscalationT1, payload);
        await _notifications.AddAsync(t1Notif, ct);
        await _notifications.SaveChangesAsync(ct);
        await _realtime.SendNotificationAsync(engineer.Id, NotificationDto.From(t1Notif), ct);

        if (!isWeekend)
        {
            var engineerBody = $"""
                <p>Hi {engineer.Name},</p>
                <p>Your task <strong>{title}</strong> is due tomorrow (<strong>{dueDate:D}</strong>).</p>
                <p>Please confirm you're on track, or flag a blocker in Pulse if you need help.</p>
                """;
            _emailQueue.Enqueue(engineer.Email, $"Task due tomorrow: {title}", EmailTemplate.Layout(engineerBody));

            var teamLead = ownerOnly ? null : await GetTeamLeadAsync(engineer, ct);
            if (teamLead is not null && teamLead.Id != engineer.Id)
            {
                var leadNotif = Notification.Create(teamLead.Id, NotificationKind.EscalationT1, payload);
                await _notifications.AddAsync(leadNotif, ct);
                await _notifications.SaveChangesAsync(ct);
                await _realtime.SendNotificationAsync(teamLead.Id, NotificationDto.From(leadNotif), ct);

                var leadBody = $"""
                    <p>Hi {teamLead.Name},</p>
                    <p><strong>{engineer.Name}</strong>'s task <strong>{title}</strong> is due tomorrow (<strong>{dueDate:D}</strong>).</p>
                    <p>Please follow up with them immediately.</p>
                    """;
                _emailQueue.Enqueue(teamLead.Email, $"Team task due tomorrow: {title}", EmailTemplate.Layout(leadBody));
            }
        }
    }

    private async Task<Engineer?> GetTeamLeadAsync(Engineer engineer, CancellationToken ct)
    {
        if (!engineer.TeamId.HasValue) return null;
        var team = await _teams.GetByIdAsync(engineer.TeamId.Value, ct);
        if (team?.TeamLeadId is null) return null;
        return await _engineers.GetByIdAsync(team.TeamLeadId.Value, ct);
    }

    private async Task FireOverdueAsync(Guid taskId, Guid projectId, string title, DateOnly dueDate,
        Guid? assigneeId, string payload, bool isWeekend, bool isQaTask, bool ownerOnly, CancellationToken ct)
    {
        var created = new List<Notification>();

        Engineer? assignee = assigneeId.HasValue
            ? await _engineers.GetByIdAsync(assigneeId.Value, ct)
            : null;
        var assigneeLabel = assignee?.Name ?? "Unassigned";

        // Notify engineer
        if (assignee is not null)
        {
            var n = Notification.Create(assignee.Id, NotificationKind.EscalationOverdue, payload);
            await _notifications.AddAsync(n, ct);
            created.Add(n);

            if (!isWeekend)
            {
                var assigneeBody = $"""
                    <p>Hi {assignee.Name},</p>
                    <p>Your task <strong>{title}</strong> was due on <strong>{dueDate:D}</strong> and is now overdue.</p>
                    <p>Please update the status or flag a blocker in Pulse immediately.</p>
                    """;
                _emailQueue.Enqueue(assignee.Email, $"Overdue task: {title}", EmailTemplate.Layout(assigneeBody));
            }
        }

        // A QA review task overdue only concerns the QA assignee's own team lead and PMO —
        // not the full PM/department-head broadcast a regular task gets.
        List<Engineer> managers;
        if (ownerOnly)
        {
            // A personal to-do concerns its owner alone.
            managers = [];
        }
        else if (isQaTask)
        {
            managers = [];
            if (assignee is not null)
            {
                var qaTeamLead = await GetTeamLeadAsync(assignee, ct);
                if (qaTeamLead is not null && qaTeamLead.Id != assignee.Id)
                    managers.Add(qaTeamLead);
            }
            managers.AddRange(await _engineers.ListByRoleAsync(Roles.HeadOfPmo, ct));
            managers = managers.DistinctBy(m => m.Id).ToList();
        }
        else
        {
            // Notify PMs and department heads — but only ones who could actually open this task.
            // Every PM passes (org-wide access), but a department head outside this project's own
            // department otherwise got the same "overdue" ping as everyone else and just hit a
            // 403 trying to look at it — noise with no action attached.
            var headEngineers = new List<Engineer>();
            foreach (var headRole in Roles.HeadRoles)
                headEngineers.AddRange(await _engineers.ListByRoleAsync(headRole, ct));
            var candidateManagers = (await _engineers.ListByRoleAsync(Roles.ProjectManager, ct))
                .Concat(headEngineers)
                .ToList();

            // Sequential — DbContext is not thread-safe.
            managers = new List<Engineer>();
            foreach (var candidate in candidateManagers)
                if (await _access.CanAccessProjectAsync(projectId, candidate.Id, candidate.Role, ct))
                    managers.Add(candidate);
        }

        foreach (var manager in managers)
        {
            var n = Notification.Create(manager.Id, NotificationKind.EscalationOverdue, payload);
            await _notifications.AddAsync(n, ct);
            created.Add(n);

            if (!isWeekend)
            {
                var managerBody = $"""
                    <p>Hi {manager.Name},</p>
                    <p>Task <strong>{title}</strong> (assigned to <strong>{assigneeLabel}</strong>) was due on <strong>{dueDate:D}</strong> and is now overdue.</p>
                    <p>Please review this task in Pulse and take appropriate action.</p>
                    """;
                _emailQueue.Enqueue(manager.Email, $"Overdue task alert: {title}", EmailTemplate.Layout(managerBody));
            }
        }

        await _notifications.SaveChangesAsync(ct);

        foreach (var n in created)
            await _realtime.SendNotificationAsync(n.UserId, NotificationDto.From(n), ct);
    }
}
