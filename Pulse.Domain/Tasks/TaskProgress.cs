namespace Pulse.Domain.Tasks;

/// <summary>
/// How far along a task is, as one percentage, worked out one way everywhere.
/// <list type="bullet">
/// <item>Backlog is 0 and Done is 100.</item>
/// <item>In QA counts as 90: the engineer's part is finished, only the review is left.</item>
/// <item>Otherwise a task with a checklist is as far along as its checklist, and one without is as far along as the engineer or lead says, in steps of
/// 25. Either way work still in flight stops at 75, because 100 is reached only by finishing the task.</item>
/// </list>
/// Earned points are the task's points times this, so they build up as work moves and are taken back if it moves backwards (a QA rejection).
/// </summary>
public static class TaskProgress
{
    public const int InQa = 90;
    public const int InFlightCap = 75;

    /// <summary>The steps an engineer or lead may set by hand. 100 is not one of them: that is what Done means.</summary>
    public static readonly IReadOnlyList<int> ManualSteps = [0, 25, 50, 75];

    public static int Effective(TaskStatus status, int manualPercent, int subtasksDone, int subtasksTotal)
    {
        switch (status)
        {
            case TaskStatus.Backlog: return 0;
            case TaskStatus.Done: return 100;
            case TaskStatus.InQa: return InQa;
        }

        var raw = subtasksTotal > 0 ? subtasksDone * 100 / subtasksTotal : manualPercent;
        return Math.Clamp(raw, 0, InFlightCap);
    }

    /// <summary>What a task has earned at a given progress. Three decimals is plenty: one point at 25% is 0.25.</summary>
    public static decimal EarnedAt(int points, int effectivePercent) =>
        Math.Round(points * (decimal)effectivePercent / 100m, 3);

    /// <summary>What is still to do: points not yet earned.</summary>
    public static decimal Remaining(int points, int effectivePercent) => points - EarnedAt(points, effectivePercent);
}
