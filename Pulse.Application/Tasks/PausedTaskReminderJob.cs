using System.Net;
using System.Text.Json;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Notifications;
using Pulse.Application.Overwork;
using Pulse.Domain.Engineers;
using Pulse.Domain.Notifications;

namespace Pulse.Application.Tasks;

/// <summary>A paused task drops out of escalations and out of the workload count, so left alone it can sit paused for good without anyone being
/// told. Each weekday morning this finds tasks paused for a whole number of weeks of working days (five by default, then ten, and so on) and tells the
/// people who can decide what happens next — the lead of the team that owns the task, that team's department head(s), and the PMO — to resume or
/// reassign it. One alert and one email per person, listing their tasks, not one message per task. It only reminds: it never reassigns.
/// A task paused by a project hold is left out, since that is a deliberate project decision and it resumes with the project.</summary>
public class PausedTaskReminderJob : IRecurringJob
{
    private readonly ITaskRepository _tasks;
    private readonly IProjectRepository _projects;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;
    private readonly INotificationDispatcher _notify;
    private readonly IAppSettings _settings;
    private readonly OverworkThresholds _thresholds;
    private readonly TimeProvider _clock;

    public PausedTaskReminderJob(
        ITaskRepository tasks, IProjectRepository projects, IEngineerRepository engineers, ITeamRepository teams,
        IAppSettings settings, OverworkThresholds thresholds, INotificationDispatcher notify, TimeProvider? clock = null)
    {
        _tasks = tasks;
        _projects = projects;
        _engineers = engineers;
        _teams = teams;
        _notify = notify;
        _settings = settings;
        _thresholds = thresholds;
        _clock = clock ?? TimeProvider.System;
    }

    private sealed record Line(Domain.Tasks.PulseTask Task, int WorkingDays);

    public async Task RunAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);
        var every = Math.Max(1, _thresholds.PausedTaskReminderBusinessDays);

        // Who to tell depends only on the project and the assignee, so work it out once per pair.
        var stakeholders = new Dictionary<(Guid, Guid?), TaskStakeholders>();
        var perPerson = new Dictionary<Guid, (Engineer Person, List<Line> Lines)>();
        var due = new List<Domain.Tasks.PulseTask>();

        foreach (var task in await _tasks.GetPausedTasksAsync(ct))
        {
            var pausedOn = DateOnly.FromDateTime(task.PausedAt!.Value);
            var days = BusinessDays.Between(pausedOn, today);
            if (days < every) continue;
            // Reminded already within the last stretch: wait until another full one has passed.
            if (task.PauseRemindedAt is DateTime last && BusinessDays.Between(DateOnly.FromDateTime(last), today) < every) continue;

            var key = (task.ProjectId, task.AssigneeId);
            if (!stakeholders.TryGetValue(key, out var who))
                stakeholders[key] = who = await TaskStakeholders.ResolveAsync(task.ProjectId, task.AssigneeId, _projects, _engineers, _teams, ct);

            var people = new List<Engineer>();
            if (who.TeamLead is not null) people.Add(who.TeamLead);
            people.AddRange(who.DepartmentHeads);
            people.AddRange(who.Pmo);

            // The person who paused it is not told to deal with their own pause.
            var recipients = people.DistinctBy(p => p.Id).Where(p => p.Id != task.AssigneeId).ToList();
            if (recipients.Count == 0) continue;

            foreach (var person in recipients)
            {
                if (!perPerson.TryGetValue(person.Id, out var entry))
                    perPerson[person.Id] = entry = (person, []);
                entry.Lines.Add(new Line(task, days));
            }
            due.Add(task);
        }

        if (perPerson.Count == 0) return;

        var projectNames = await _projects.GetNamesByIdsAsync(due.Select(t => t.ProjectId).Distinct().ToList(), ct);
        var assigneeIds = due.Where(t => t.AssigneeId.HasValue).Select(t => t.AssigneeId!.Value).Distinct().ToList();
        var assigneeNames = (await _engineers.GetByIdsAsync(assigneeIds, ct)).ToDictionary(e => e.Id, e => e.Name);

        foreach (var (person, lines) in perPerson.Values)
            await SendAsync(person, lines, projectNames, assigneeNames, ct);

        foreach (var task in due)
            task.MarkPauseReminded(now);
        await _tasks.SaveChangesAsync(ct);
    }

    private async Task SendAsync(Engineer recipient, List<Line> lines, IReadOnlyDictionary<Guid, string> projectNames,
        IReadOnlyDictionary<Guid, string> assigneeNames, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { count = lines.Count });
        await _notify.NotifyAsync(recipient.Id, NotificationKind.PausedTaskReminder, payload, ct: ct);

        var sections = string.Concat(lines
            .GroupBy(l => l.Task.ProjectId)
            .OrderBy(g => projectNames.GetValueOrDefault(g.Key, "Project"), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var items = string.Concat(g.OrderByDescending(l => l.WorkingDays).ThenBy(l => l.Task.Title).Select(l => Item(l, assigneeNames)));
                var name = WebUtility.HtmlEncode(projectNames.GetValueOrDefault(g.Key, "Project"));
                return $"<h4 style=\"margin:16px 0 4px\">{name} ({g.Count()})</h4><ul>{items}</ul>";
            }));

        var body = $"""
            <p>Hi {WebUtility.HtmlEncode(recipient.Name)},</p>
            <p>{lines.Count} task{(lines.Count == 1 ? " has" : "s have")} been paused for a week or more. Please decide whether to resume {(lines.Count == 1 ? "it" : "them")} or reassign {(lines.Count == 1 ? "it" : "them")}:</p>
            {sections}
            {EmailTemplate.Button($"{_settings.AppBaseUrl}/tasks", "Open tasks")}
            {EmailTemplate.Muted("You are told because you lead the team, head the department or are part of the PMO that this work belongs to. A paused task is not counted in anyone's workload or in due-date alerts.")}
            """;
        await _notify.EmailAsync(recipient.Id, NotificationKind.PausedTaskReminder, new NotificationEmail(recipient.Email,
            lines.Count == 1 ? "Paused task needs a decision: 1 task" : $"Paused tasks need a decision: {lines.Count} tasks",
            EmailTemplate.Layout(body)), ct);
    }

    private static string Item(Line line, IReadOnlyDictionary<Guid, string> assigneeNames)
    {
        var who = line.Task.AssigneeId is Guid id && assigneeNames.TryGetValue(id, out var name) ? name : "Unassigned";
        var note = string.IsNullOrWhiteSpace(line.Task.PauseNote) ? "" : $" — “{WebUtility.HtmlEncode(line.Task.PauseNote.Trim())}”";
        return $"<li><strong>{WebUtility.HtmlEncode(line.Task.Title)}</strong> — {WebUtility.HtmlEncode(who)} · paused {line.WorkingDays} working days{note}</li>";
    }
}
