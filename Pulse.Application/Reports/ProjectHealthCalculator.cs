using Pulse.Application.Overwork;

namespace Pulse.Application.Reports;

/// <summary>One task that contributes to a project's health, for naming it in the report.
/// <paramref name="Days"/> means "working days late" for an overdue task, "days until due" for one due soon,
/// and "working days blocked" for a blocker.</summary>
public record HealthTaskRef(Guid TaskId, string? Key, string Title, string? AssigneeName, int Days);

/// <param name="Kind">"overdue" | "blocked_long" | "blocked" | "due_soon"</param>
/// <param name="Action">What to do about it, in a few words.</param>
public record HealthReasonDto(string Kind, int Count, string Text, IReadOnlyList<HealthTaskRef> Examples, string Action = "");

public record ProjectHealthAssessment(
    string Health, IReadOnlyList<HealthReasonDto> Reasons, IReadOnlyList<string> NextSteps);

/// <summary>
/// The one rule behind every "Healthy / At Risk / Critical" pill, so the status always comes with its
/// reasons and a way out:
///
///   Critical — something has been stuck or late for too long, or several things are late:
///              a blocker has sat <see cref="OverworkThresholds.BlockerCriticalBusinessDays"/> or more working
///              days; a task is <see cref="OverworkThresholds.OverdueCriticalBusinessDays"/> or more working days
///              late; or <see cref="OverworkThresholds.CriticalOverdueTasks"/> tasks are late past the
///              <see cref="OverworkThresholds.OverdueGraceBusinessDays"/>-day grace.
///   At Risk  — something is late (but recently), blocked (but not for long), or due within the escalation
///              window (3 days by default).
///   Healthy  — none of the above.
///
/// Lateness is judged by how late, so one task a day behind never makes a project Critical, whatever the
/// project's size or how big a slice of it a team report shows.
///
/// "Overdue" and "due soon" come from <see cref="Escalations.EscalationLevelCalculator"/> (Overdue vs
/// T-1/T-3), so the status agrees with the Escalations page and the notifications people actually get.
/// </summary>
public static class ProjectHealthCalculator
{
    // A ceiling against a pathological project, not a realistic cap — no real project has
    // anywhere near 50 overdue/blocked tasks in one reason, so in practice this sends the full
    // list, and the frontend's own "Show all" reveal only ever needs to work with what's here.
    private const int MaxExamples = 50;

    public static ProjectHealthAssessment Assess(
        IReadOnlyList<HealthTaskRef> overdue,
        IReadOnlyList<HealthTaskRef> dueSoon,
        IReadOnlyList<HealthTaskRef> blocked,
        OverworkThresholds thresholds)
    {
        var blockerDays = thresholds.BlockerCriticalBusinessDays;
        var overdueSorted = overdue.OrderByDescending(t => t.Days).ToList();
        var dueSoonSorted = dueSoon.OrderBy(t => t.Days).ToList();
        var blockedLong = blocked.Where(t => t.Days >= blockerDays).OrderByDescending(t => t.Days).ToList();
        var blockedRecent = blocked.Where(t => t.Days < blockerDays).OrderByDescending(t => t.Days).ToList();
        var dueSoonDays = (int)Math.Ceiling(thresholds.EscalationT3Days);

        var reasons = new List<HealthReasonDto>();
        var steps = new List<string>();

        if (overdueSorted.Count > 0)
        {
            var oldest = overdueSorted[0].Days;
            var n = overdueSorted.Count;
            reasons.Add(new("overdue", n,
                $"{n} overdue {Plural(n, "task")} (oldest {oldest} working {Plural(oldest, "day")} late)",
                Take(overdueSorted), "Finish, re-date or reassign"));
            steps.Add($"Finish, re-date or reassign the {n} overdue {Plural(n, "task")} " +
                      $"(longest: {oldest} working {Plural(oldest, "day")} late).");
        }
        if (blockedLong.Count > 0)
        {
            var n = blockedLong.Count;
            reasons.Add(new("blocked_long", n,
                $"{n} blocked {blockerDays}+ working days (oldest {blockedLong[0].Days})", Take(blockedLong),
                "Unblock or escalate"));
            steps.Add($"Unblock the {n} {Plural(n, "task")} stuck {blockerDays}+ working days (oldest: {blockedLong[0].Days}).");
        }
        if (blockedRecent.Count > 0)
        {
            var n = blockedRecent.Count;
            reasons.Add(new("blocked", n, $"{n} blocked", Take(blockedRecent),
                $"Follow up before {(n == 1 ? "it reaches" : "they reach")} {blockerDays} working days"));
            steps.Add($"Unblock or follow up on the {n} blocked {Plural(n, "task")} " +
                      $"before {(n == 1 ? "it reaches" : "they reach")} {blockerDays} working days.");
        }
        if (dueSoonSorted.Count > 0)
        {
            var n = dueSoonSorted.Count;
            reasons.Add(new("due_soon", n, $"{n} due within {dueSoonDays} days", Take(dueSoonSorted), "Check they're on track"));
            steps.Add($"Check that the {n} {Plural(n, "task")} due within {dueSoonDays} days {(n == 1 ? "is" : "are")} on track.");
        }

        var pastGrace = overdueSorted.Count(t => t.Days > thresholds.OverdueGraceBusinessDays);
        var overdueIsCritical = overdueSorted.Any(t => t.Days >= thresholds.OverdueCriticalBusinessDays)
            || pastGrace >= thresholds.CriticalOverdueTasks;

        var health = overdueIsCritical || blockedLong.Count > 0 ? "Critical"
            : overdueSorted.Count > 0 || dueSoonSorted.Count > 0 || blockedRecent.Count > 0 ? "AtRisk"
            : "Healthy";

        return new ProjectHealthAssessment(health, reasons, steps);
    }

    private static IReadOnlyList<HealthTaskRef> Take(List<HealthTaskRef> sorted) => sorted.Take(MaxExamples).ToList();

    private static string Plural(int n, string word) => n == 1 ? word : word + "s";
}

/// <summary>Builds <see cref="ProjectHealthCalculator"/>'s per-project inputs from the task lists the
/// reports already load, so the PMO report and the weekly workstream table can't drift apart.</summary>
internal static class ProjectHealthInputs
{
    /// <summary>Splits escalating tasks into overdue vs due-soon (via EscalationLevelCalculator), per project.</summary>
    public static (Dictionary<Guid, List<HealthTaskRef>> Overdue, Dictionary<Guid, List<HealthTaskRef>> DueSoon) FromEscalations(
        IEnumerable<Domain.Tasks.PulseTask> candidates,
        Func<Domain.Tasks.PulseTask, bool> include,
        IReadOnlyDictionary<Guid, string> codeByProject,
        IReadOnlyDictionary<Guid, string> nameByEngineer,
        OverworkThresholds thresholds,
        DateTime now)
    {
        var overdue = new Dictionary<Guid, List<HealthTaskRef>>();
        var dueSoon = new Dictionary<Guid, List<HealthTaskRef>>();
        var today = DateOnly.FromDateTime(now);

        foreach (var t in candidates)
        {
            if (t.DueDate is null || !include(t)) continue;
            var level = Escalations.EscalationLevelCalculator.Determine(t.DueDate.Value, t.ActivatedAt, thresholds, now);
            if (level is null) continue;

            if (level == Domain.Escalations.EscalationLevel.Overdue)
                // Working days late (at least 1 — the due date has passed, even if only a weekend has since gone by).
                Add(overdue, t, Math.Max(1, Common.BusinessDays.Between(t.DueDate.Value, today)), codeByProject, nameByEngineer);
            else
                Add(dueSoon, t, Math.Max(0, t.DueDate.Value.DayNumber - today.DayNumber), codeByProject, nameByEngineer);
        }
        return (overdue, dueSoon);
    }

    /// <summary>Blocked tasks per project, aged in working days since they last became blocked (falling back to
    /// when the task was activated for any blocked before history recorded it).</summary>
    public static Dictionary<Guid, List<HealthTaskRef>> FromBlocked(
        IEnumerable<Domain.Tasks.PulseTask> blockedTasks,
        Func<Domain.Tasks.PulseTask, bool> include,
        IReadOnlyDictionary<Guid, DateTime> blockedSince,
        IReadOnlyDictionary<Guid, string> codeByProject,
        IReadOnlyDictionary<Guid, string> nameByEngineer,
        DateTime now)
    {
        var result = new Dictionary<Guid, List<HealthTaskRef>>();
        var today = DateOnly.FromDateTime(now);
        foreach (var t in blockedTasks)
        {
            if (!include(t)) continue;
            var since = blockedSince.TryGetValue(t.Id, out var at) ? at : t.ActivatedAt;
            Add(result, t, Common.BusinessDays.Between(DateOnly.FromDateTime(since), today), codeByProject, nameByEngineer);
        }
        return result;
    }

    public static int BlockedDays(Domain.Tasks.PulseTask t, IReadOnlyDictionary<Guid, DateTime> blockedSince, DateTime now) =>
        (int)(now - (blockedSince.TryGetValue(t.Id, out var at) ? at : t.ActivatedAt)).TotalDays;

    private static void Add(Dictionary<Guid, List<HealthTaskRef>> map, Domain.Tasks.PulseTask t, int days,
        IReadOnlyDictionary<Guid, string> codeByProject, IReadOnlyDictionary<Guid, string> nameByEngineer)
    {
        if (!map.TryGetValue(t.ProjectId, out var list)) map[t.ProjectId] = list = [];
        var key = codeByProject.TryGetValue(t.ProjectId, out var code) ? $"{code}-{t.TaskNumber}" : null;
        var assignee = t.AssigneeId.HasValue && nameByEngineer.TryGetValue(t.AssigneeId.Value, out var n) ? n : null;
        list.Add(new HealthTaskRef(t.Id, key, t.Title, assignee, days));
    }
}
