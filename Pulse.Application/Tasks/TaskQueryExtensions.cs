using Pulse.Domain.Tasks;

namespace Pulse.Application.Tasks;

/// <summary>
/// A QA sub-task (ParentTaskId set) carries its own copy of the original task's points and,
/// once accepted, reaches Done independently — so summing points across "every task" double
/// counts a single piece of delivered work for any task that went through QA. Apply this to
/// every "delivered/planned scope" metric — sprint velocity, burndown, throughput, delivered-
/// points reports, and individual/team performance stats — so a task's points reconcile to
/// exactly one place no matter how the numbers are sliced. Deliberately NOT applied to "current
/// workload" metrics (overwork detection, EngineerUtilizationEntry.TotalPoints) — a QA
/// reviewer's active review genuinely occupies part of their plate right now, so that one is
/// correct as-is; it measures capacity, not delivered scope.
/// </summary>
public static class TaskQueryExtensions
{
    public static IQueryable<PulseTask> ExcludingQaSubtasks(this IQueryable<PulseTask> tasks) =>
        tasks.Where(t => t.ParentTaskId == null);

    public static IEnumerable<PulseTask> ExcludingQaSubtasks(this IEnumerable<PulseTask> tasks) =>
        tasks.Where(t => t.ParentTaskId == null);
}
