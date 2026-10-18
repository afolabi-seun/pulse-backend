using System.Net;
using System.Text.Json;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Notifications;
using Pulse.Domain.Engineers;
using Pulse.Domain.Escalations;
using Pulse.Domain.Notifications;

namespace Pulse.Application.Escalations;

/// <summary>
/// One due-date digest per department head and PMO member, each weekday morning, instead of an alert and an email for every task. A head or the PMO can have
/// dozens of tasks across projects, and a message per task per stage buries the ones that matter.
///
/// Who sees what (the assignee and the team lead are told per task by <see cref="EscalationScanner"/>, not here):
///   - divisional heads: tasks that reached the 3-day stage, and overdue ones;
///   - the PMO (Head of PMO and Project Managers): tasks that reached the 1-day stage, and overdue ones.
/// Only projects the person can open count. The digest covers what fired since that person's previous digest (or the last three days the first time), so a
/// Monday digest includes the weekend, and a task is listed once, in the state it is in now. Tasks that have since been finished, paused, sent to QA or
/// returned to Backlog are left out, as are QA review tasks (their own per-task path) and personal to-dos.
/// </summary>
public class EscalationDigestJob : IRecurringJob
{
    private static readonly EscalationLevel[] HeadLevels = [EscalationLevel.TMinus3, EscalationLevel.Overdue];
    private static readonly EscalationLevel[] PmoLevels = [EscalationLevel.TMinus1, EscalationLevel.Overdue];

    private static readonly Domain.Tasks.TaskStatus[] NotLive =
        [Domain.Tasks.TaskStatus.Done, Domain.Tasks.TaskStatus.Backlog, Domain.Tasks.TaskStatus.Paused, Domain.Tasks.TaskStatus.InQa];

    private readonly IEscalationEventRepository _events;
    private readonly ITaskRepository _tasks;
    private readonly IProjectRepository _projects;
    private readonly IEngineerRepository _engineers;
    private readonly IProjectAccessPolicy _access;
    private readonly INotificationRepository _notifications;
    private readonly INotificationDispatcher _notify;
    private readonly IAppSettings _settings;
    private readonly TimeProvider _clock;

    public EscalationDigestJob(
        IEscalationEventRepository events,
        ITaskRepository tasks,
        IProjectRepository projects,
        IEngineerRepository engineers,
        IProjectAccessPolicy access,
        INotificationRepository notifications,
        INotificationDispatcher notify,
        IAppSettings settings,
        TimeProvider? clock = null)
    {
        _events = events;
        _tasks = tasks;
        _projects = projects;
        _engineers = engineers;
        _access = access;
        _notifications = notifications;
        _notify = notify;
        _settings = settings;
        _clock = clock ?? TimeProvider.System;
    }

    private sealed record Recipient(Engineer Person, EscalationLevel[] Levels);

    private sealed record Line(Domain.Tasks.PulseTask Task, bool Overdue);

    public async Task RunAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var firstWindow = now.AddDays(-3);

        var recipients = await ListRecipientsAsync(ct);
        if (recipients.Count == 0) return;

        var lastDigest = await _notifications.GetLastCreatedAtByUserAsync(NotificationKind.EscalationDigest, ct);
        DateTime SinceFor(Recipient r) => lastDigest.TryGetValue(r.Person.Id, out var last) ? last : firstWindow;

        var events = await _events.ListFiredSinceAsync(recipients.Min(SinceFor), ct);
        if (events.Count == 0) return;

        var candidates = (await _tasks.GetByIdsAsync(events.Select(e => e.TaskId).Distinct().ToList(), ct))
            .Where(t => !t.IsArchived && t.DueDate.HasValue && t.ParentTaskId is null && !NotLive.Contains(t.Status))
            .ToList();
        // A person's private to-dos never reach a digest.
        var personalProjectIds = await _projects.GetPersonalProjectIdsAsync(candidates.Select(t => t.ProjectId).Distinct().ToList(), ct);
        var live = candidates.Where(t => !personalProjectIds.Contains(t.ProjectId)).ToDictionary(t => t.Id);
        if (live.Count == 0) return;

        var projectNames = await _projects.GetNamesByIdsAsync(live.Values.Select(t => t.ProjectId).Distinct().ToList(), ct);
        var assigneeIds = live.Values.Where(t => t.AssigneeId.HasValue).Select(t => t.AssigneeId!.Value).Distinct().ToList();
        var assigneeNames = assigneeIds.Count == 0
            ? new Dictionary<Guid, string>()
            : (await _engineers.GetByIdsAsync(assigneeIds, ct)).ToDictionary(e => e.Id, e => e.Name);

        var canOpen = new Dictionary<(Guid, Guid), bool>();
        async Task<bool> CanOpenAsync(Engineer person, Guid projectId)
        {
            if (!canOpen.TryGetValue((person.Id, projectId), out var ok))
                canOpen[(person.Id, projectId)] = ok = await _access.CanAccessProjectAsync(projectId, person.Id, person.Role, ct);
            return ok;
        }

        foreach (var recipient in recipients)
        {
            var since = SinceFor(recipient);
            var lines = new List<Line>();
            foreach (var taskEvents in events.Where(e => e.FiredAt > since && recipient.Levels.Contains(e.Level)).GroupBy(e => e.TaskId))
            {
                if (!live.TryGetValue(taskEvents.Key, out var task)) continue;
                if (!await CanOpenAsync(recipient.Person, task.ProjectId)) continue;
                lines.Add(new Line(task, DueDateWording.DaysUntil(task.DueDate!.Value, now) < 0));
            }
            if (lines.Count == 0) continue;

            await SendAsync(recipient.Person, lines, projectNames, assigneeNames, now, ct);
        }
    }

    private async Task<List<Recipient>> ListRecipientsAsync(CancellationToken ct)
    {
        var recipients = new Dictionary<Guid, Recipient>();

        foreach (var role in Roles.HeadRoles.Where(r => r != Roles.HeadOfPmo))
            foreach (var head in await _engineers.ListByRoleAsync(role, ct))
                recipients[head.Id] = new Recipient(head, HeadLevels);

        foreach (var role in new[] { Roles.HeadOfPmo, Roles.ProjectManager })
            foreach (var pmo in await _engineers.ListByRoleAsync(role, ct))
                recipients[pmo.Id] = new Recipient(pmo, PmoLevels);

        return recipients.Values.ToList();
    }

    private async Task SendAsync(Engineer recipient, List<Line> lines, IReadOnlyDictionary<Guid, string> projectNames,
        IReadOnlyDictionary<Guid, string> assigneeNames, DateTime now, CancellationToken ct)
    {
        var overdue = lines.Count(l => l.Overdue);
        var payload = JsonSerializer.Serialize(new { count = lines.Count, overdue });

        var sections = string.Concat(lines
            .GroupBy(l => l.Task.ProjectId)
            .OrderBy(g => projectNames.GetValueOrDefault(g.Key, "Project"), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var items = string.Concat(g.OrderBy(l => l.Task.DueDate).ThenBy(l => l.Task.Title).Select(l => Item(l, assigneeNames, now)));
                var name = WebUtility.HtmlEncode(projectNames.GetValueOrDefault(g.Key, "Project"));
                return $"<h4 style=\"margin:16px 0 4px\">{name} ({g.Count()})</h4><ul>{items}</ul>";
            }));

        var body = $"""
            <p>Hi {WebUtility.HtmlEncode(recipient.Name)},</p>
            <p>{lines.Count} task{(lines.Count == 1 ? "" : "s")} in your projects reached a due-date warning since your last digest{(overdue > 0 ? $" ({overdue} overdue)" : "")}:</p>
            {sections}
            {EmailTemplate.Button($"{_settings.AppBaseUrl}/escalations", "View escalations")}
            {EmailTemplate.Muted("Assignees and team leads are told about each task as it happens; you get this once a weekday.")}
            """;
        // One call: the in-app alert always lands (it is also what marks the digest as sent), and the email and any chat message follow the person's own
        // notification preferences for this kind.
        var subject = lines.Count == 1 ? "Due-date digest: 1 task needs attention" : $"Due-date digest: {lines.Count} tasks need attention";
        await _notify.NotifyAsync(recipient.Id, NotificationKind.EscalationDigest, payload,
            new NotificationEmail(recipient.Email, subject, EmailTemplate.Layout(body)), ct);
    }

    private static string Item(Line line, IReadOnlyDictionary<Guid, string> assigneeNames, DateTime now)
    {
        var due = line.Task.DueDate!.Value;
        var who = line.Task.AssigneeId is Guid id && assigneeNames.TryGetValue(id, out var name) ? name : "Unassigned";
        var when = line.Overdue
            ? $"<strong>overdue</strong>, was due {due:D}{DueDateWording.OverdueSuffix(due, now)}"
            : $"due <strong>{DueDateWording.When(due, now)}</strong> ({due:D})";
        return $"<li><strong>{WebUtility.HtmlEncode(line.Task.Title)}</strong> — {WebUtility.HtmlEncode(who)} · {when}</li>";
    }
}
