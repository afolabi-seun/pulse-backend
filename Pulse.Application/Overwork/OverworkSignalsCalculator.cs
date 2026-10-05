using Pulse.Domain.Engineers;
using Pulse.Domain.Overrides;
using Pulse.Domain.Tasks;

namespace Pulse.Application.Overwork;

public class OverworkSignalsCalculator
{
    private readonly OverworkThresholds _thresholds;

    public OverworkSignalsCalculator(OverworkThresholds thresholds) => _thresholds = thresholds;

    public (OverworkSignals Signals, bool WouldFlagOverworked) Compute(
        Engineer engineer,
        IReadOnlyList<PulseTask> activeTasks,
        OverworkOverride? @override,
        DepartmentThresholdOverride? departmentOverride = null)
    {
        var thresholds = DepartmentThresholdResolver.Resolve(_thresholds, departmentOverride);

        if (@override?.IsActive == true)
            return (BuildSignals(engineer, activeTasks, thresholds), false);

        var signals = BuildSignals(engineer, activeTasks, thresholds);
        return (signals, signals.TrippedCount >= thresholds.SignalsRequiredToFlag);
    }

    /// <summary>Points on the engineer's active work that are due within their baseline cycle — plus anything overdue or
    /// undated. This is the number the load signal compares with the baseline, and what "due this cycle" means wherever
    /// it is shown. Zero when no baseline is configured (the signal doesn't apply).</summary>
    public static int CyclePoints(Engineer engineer, IReadOnlyList<PulseTask> activeTasks)
    {
        if (engineer.BaselinePoints <= 0 || engineer.BaselineCycleDays <= 0) return 0;
        // BaselinePoints is framed as a rate — "N points per cycle" — so the load signal only
        // counts points on tasks due within that window, not a lifetime running total. Overdue
        // tasks (due date already in the past) and undated tasks both count regardless: an
        // undated task is still real, committed work with unknown timing, and excluding it
        // would silently under-report load, the wrong failure direction for an overwork
        // detector. Only a task with a due date clearly beyond this cycle is excluded.
        var cycleEnd = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(engineer.BaselineCycleDays);
        return activeTasks.Where(t => t.DueDate is null || t.DueDate <= cycleEnd).Sum(t => t.Points);
    }

    private static OverworkSignals BuildSignals(Engineer engineer, IReadOnlyList<PulseTask> activeTasks, OverworkThresholds thresholds)
    {
        // Engineer.Create/UpdateBaseline now reject a non-positive baseline, but this stays as a
        // defensive floor against any pre-existing row (e.g. a non-estimating role seeded before
        // that guard existed) — without it, a zero baseline turns the threshold into 0 and trips
        // the load signal on any single active task, which misreports "no baseline configured" as
        // "severely overworked".
        var hasBaseline = engineer.BaselinePoints > 0 && engineer.BaselineCycleDays > 0;

        OverworkSignal loadSignal;
        var inCyclePoints = 0;
        var threshold = 0.0;
        if (hasBaseline)
        {
            inCyclePoints = CyclePoints(engineer, activeTasks);
            threshold = engineer.BaselinePoints * thresholds.LoadVsBaselineRatio;
            loadSignal = new OverworkSignal(
                inCyclePoints > threshold,
                $"{inCyclePoints} pts due this cycle > {threshold} pts threshold");
        }
        else
        {
            loadSignal = new OverworkSignal(false, "No baseline configured");
        }

        var concurrentSignal = new OverworkSignal(
            activeTasks.Count > thresholds.MaxConcurrentTasks,
            $"{activeTasks.Count} concurrent tasks > {thresholds.MaxConcurrentTasks} limit");

        var staleDays = engineer.BaselineCycleDays * thresholds.StaleCycleMultiplier;
        var staleTask = hasBaseline
            ? activeTasks.FirstOrDefault(t => (DateTime.UtcNow - t.ActivatedAt).TotalDays > staleDays)
            : null;

        var staleSignal = new OverworkSignal(
            staleTask is not null,
            staleTask is not null
                ? $"Task '{staleTask.Title}' active for > {staleDays:F1} days"
                : "No stale tasks");

        return new OverworkSignals
        {
            LoadVsBaseline = loadSignal,
            Concurrent = concurrentSignal,
            StaleInProgress = staleSignal,
            ActivePoints = activeTasks.Sum(t => t.Points),
            CyclePoints = inCyclePoints,
            CycleDays = engineer.BaselineCycleDays,
            LoadThresholdPoints = threshold,
        };
    }
}
