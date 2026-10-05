using Pulse.Application.Overwork;
using Pulse.Domain.Engineers;
using Pulse.Domain.Overrides;
using Pulse.Domain.Tasks;
using FluentAssertions;

namespace Pulse.UnitTests.Overwork;

public class OverworkSignalsCalculatorTests
{
    private readonly OverworkThresholds _thresholds = new();

    private OverworkSignalsCalculator CreateCalculator() => new(_thresholds);

    private static Engineer NewEngineer(int baselinePoints = 20, int baselineCycleDays = 14) =>
        Engineer.Create("Dev", "dev@pulse.io", "pw", Roles.Engineer, baselinePoints, baselineCycleDays);

    // dueDaysFromNow defaults to today — always inside any cycle window — so every existing
    // caller of this helper keeps testing what it always tested (concurrent/stale signals, or a
    // load signal that's meant to count) without needing to know about the windowing rule.
    private static PulseTask ActiveTask(int points, int activatedDaysAgo = 0, int? dueDaysFromNow = 0) =>
        CreateTaskWithAge(points, activatedDaysAgo, dueDaysFromNow);

    private static PulseTask CreateTaskWithAge(int points, int activatedDaysAgo, int? dueDaysFromNow = 0)
    {
        var task = PulseTask.Create($"Task-{points}pts", points, Guid.NewGuid(),
            dueDate: dueDaysFromNow.HasValue ? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(dueDaysFromNow.Value)) : null);
        if (activatedDaysAgo > 0)
            typeof(PulseTask).GetProperty("ActivatedAt")!.SetValue(task, DateTime.UtcNow.AddDays(-activatedDaysAgo));
        return task;
    }

    // The load signal sums points across every active task, and a real engineer's total load
    // exceeding baseline is normally several tasks, not one — PulseTask itself caps a single
    // task at 13 points (the story-point scale), so a test wanting a load total above that has to
    // spread it across multiple same-aged, same-due-date tasks rather than one oversized one.
    private static PulseTask[] ActiveTasks(int totalPoints, int activatedDaysAgo = 0, int? dueDaysFromNow = 0)
    {
        var tasks = new List<PulseTask>();
        var remaining = totalPoints;
        while (remaining > 0)
        {
            var chunk = Math.Min(remaining, 13);
            tasks.Add(CreateTaskWithAge(chunk, activatedDaysAgo, dueDaysFromNow));
            remaining -= chunk;
        }
        return tasks.ToArray();
    }

    // ── Load signal ───────────────────────────────────────────────────────────

    [Fact]
    public void Load_signal_trips_when_points_exceed_baseline_ratio()
    {
        // baseline 20, ratio 1.3 → threshold 26; 27 pts > 26
        var engineer = NewEngineer(baselinePoints: 20);
        var tasks = ActiveTasks(27);

        var (signals, _) = CreateCalculator().Compute(engineer, tasks, null);

        signals.LoadVsBaseline.Tripped.Should().BeTrue();
    }

    [Fact]
    public void Load_signal_does_not_trip_when_points_at_baseline()
    {
        // baseline 20, ratio 1.3 → threshold 26; exactly 20 pts
        var engineer = NewEngineer(baselinePoints: 20);
        var tasks = ActiveTasks(20);

        var (signals, _) = CreateCalculator().Compute(engineer, tasks, null);

        signals.LoadVsBaseline.Tripped.Should().BeFalse();
    }

    [Fact]
    public void Load_signal_does_not_trip_when_no_tasks()
    {
        var engineer = NewEngineer();

        var (signals, _) = CreateCalculator().Compute(engineer, [], null);

        signals.LoadVsBaseline.Tripped.Should().BeFalse();
    }

    [Fact]
    public void Load_and_stale_signals_do_not_trip_for_a_zero_baseline_engineer()
    {
        // Engineer.Create/UpdateBaseline reject a non-positive baseline going forward, but this
        // guards any pre-existing row from before that check existed — without it, a zero
        // baseline turns the threshold into 0 pts, tripping the load signal on any single active
        // task and misreporting "no baseline configured" as "severely overworked".
        var engineer = NewEngineer(baselinePoints: 20, baselineCycleDays: 14);
        typeof(Engineer).GetProperty(nameof(Engineer.BaselinePoints))!.SetValue(engineer, 0);
        var tasks = new[] { ActiveTask(1, activatedDaysAgo: 100) };

        var (signals, wouldFlag) = CreateCalculator().Compute(engineer, tasks, null);

        signals.LoadVsBaseline.Tripped.Should().BeFalse();
        signals.StaleInProgress.Tripped.Should().BeFalse();
        wouldFlag.Should().BeFalse();
    }

    [Fact]
    public void Load_signal_excludes_points_on_tasks_due_beyond_the_current_cycle()
    {
        // baseline 20, ratio 1.3 -> threshold 26; 27 pts would trip it, but the task is due
        // 30 days out against a 14-day cycle, so it shouldn't count at all.
        var engineer = NewEngineer(baselinePoints: 20, baselineCycleDays: 14);
        var tasks = ActiveTasks(27, dueDaysFromNow: 30);

        var (signals, _) = CreateCalculator().Compute(engineer, tasks, null);

        signals.LoadVsBaseline.Tripped.Should().BeFalse();
    }

    [Fact]
    public void Load_signal_includes_overdue_tasks_regardless_of_cycle_window()
    {
        var engineer = NewEngineer(baselinePoints: 20, baselineCycleDays: 14);
        var tasks = ActiveTasks(27, dueDaysFromNow: -5);

        var (signals, _) = CreateCalculator().Compute(engineer, tasks, null);

        signals.LoadVsBaseline.Tripped.Should().BeTrue();
    }

    [Fact]
    public void Load_signal_includes_tasks_with_no_due_date()
    {
        var engineer = NewEngineer(baselinePoints: 20, baselineCycleDays: 14);
        var tasks = ActiveTasks(27, dueDaysFromNow: null);

        var (signals, _) = CreateCalculator().Compute(engineer, tasks, null);

        signals.LoadVsBaseline.Tripped.Should().BeTrue();
    }

    [Fact]
    public void Load_signal_includes_tasks_due_right_at_the_edge_of_the_cycle()
    {
        var engineer = NewEngineer(baselinePoints: 20, baselineCycleDays: 14);
        var tasks = ActiveTasks(27, dueDaysFromNow: 14);

        var (signals, _) = CreateCalculator().Compute(engineer, tasks, null);

        signals.LoadVsBaseline.Tripped.Should().BeTrue();
    }

    // ── Concurrent signal ─────────────────────────────────────────────────────

    [Fact]
    public void Concurrent_signal_trips_when_task_count_exceeds_max()
    {
        // default max concurrent = 3; 4 tasks > 3
        var engineer = NewEngineer();
        var tasks = Enumerable.Range(0, 4).Select(_ => ActiveTask(1)).ToList();

        var (signals, _) = CreateCalculator().Compute(engineer, tasks, null);

        signals.Concurrent.Tripped.Should().BeTrue();
    }

    [Fact]
    public void Concurrent_signal_does_not_trip_at_exactly_max_tasks()
    {
        var engineer = NewEngineer();
        var tasks = Enumerable.Range(0, 3).Select(_ => ActiveTask(1)).ToList();

        var (signals, _) = CreateCalculator().Compute(engineer, tasks, null);

        signals.Concurrent.Tripped.Should().BeFalse();
    }

    // ── Stale signal ──────────────────────────────────────────────────────────

    [Fact]
    public void Stale_signal_trips_when_task_older_than_cycle_multiplier()
    {
        // baselineCycleDays 14, multiplier 1.5 → threshold 21 days; task activated 22 days ago
        var engineer = NewEngineer(baselineCycleDays: 14);
        var tasks = new[] { ActiveTask(1, activatedDaysAgo: 22) };

        var (signals, _) = CreateCalculator().Compute(engineer, tasks, null);

        signals.StaleInProgress.Tripped.Should().BeTrue();
    }

    [Fact]
    public void Stale_signal_does_not_trip_for_fresh_tasks()
    {
        var engineer = NewEngineer(baselineCycleDays: 14);
        var tasks = new[] { ActiveTask(1, activatedDaysAgo: 1) };

        var (signals, _) = CreateCalculator().Compute(engineer, tasks, null);

        signals.StaleInProgress.Tripped.Should().BeFalse();
    }

    // ── WouldFlagOverworked ───────────────────────────────────────────────────

    [Fact]
    public void Compute_flags_overwork_when_two_signals_trip()
    {
        // Trips load (27 pts > 26 threshold) + concurrent (4 > 3)
        var engineer = NewEngineer(baselinePoints: 20);
        var tasks = Enumerable.Range(0, 4).Select(_ => ActiveTask(7)).ToList(); // 28 pts, 4 tasks

        var (_, wouldFlag) = CreateCalculator().Compute(engineer, tasks, null);

        wouldFlag.Should().BeTrue();
    }

    [Fact]
    public void Compute_does_not_flag_when_only_one_signal_trips()
    {
        // Only concurrent trips (4 tasks), load is fine (4 pts total < 26)
        var engineer = NewEngineer(baselinePoints: 20);
        var tasks = Enumerable.Range(0, 4).Select(_ => ActiveTask(1)).ToList(); // 4 pts, 4 tasks

        var (_, wouldFlag) = CreateCalculator().Compute(engineer, tasks, null);

        wouldFlag.Should().BeFalse();
    }

    [Fact]
    public void Compute_does_not_flag_when_no_signals_trip()
    {
        var engineer = NewEngineer(baselinePoints: 20);
        var tasks = new[] { ActiveTask(5) };

        var (_, wouldFlag) = CreateCalculator().Compute(engineer, tasks, null);

        wouldFlag.Should().BeFalse();
    }

    // ── Override suppression ──────────────────────────────────────────────────

    [Fact]
    public void Compute_returns_false_when_active_override_exists_regardless_of_signals()
    {
        // All three signals trip
        var engineer = NewEngineer(baselinePoints: 20, baselineCycleDays: 14);
        var tasks = Enumerable.Range(0, 4).Select(_ => ActiveTask(7, activatedDaysAgo: 22)).ToList();
        var activeOverride = OverworkOverride.Grant(engineer.Id, "crunch", DateTime.UtcNow.AddDays(7), Guid.NewGuid());

        var (_, wouldFlag) = CreateCalculator().Compute(engineer, tasks, activeOverride);

        wouldFlag.Should().BeFalse();
    }

    [Fact]
    public void Compute_flags_when_expired_override_exists_and_signals_trip()
    {
        var engineer = NewEngineer(baselinePoints: 20);
        var tasks = Enumerable.Range(0, 4).Select(_ => ActiveTask(7)).ToList();
        // Expired override (in the past)
        var expiredOverride = OverworkOverride.Grant(engineer.Id, "old crunch", DateTime.UtcNow.AddDays(-1), Guid.NewGuid());

        var (_, wouldFlag) = CreateCalculator().Compute(engineer, tasks, expiredOverride);

        wouldFlag.Should().BeTrue();
    }

    // ── TrippedCount ──────────────────────────────────────────────────────────

    [Fact]
    public void TrippedCount_reflects_number_of_tripped_signals()
    {
        var engineer = NewEngineer(baselinePoints: 20, baselineCycleDays: 14);
        // All three signals trip: load (28pts > 26), concurrent (4 > 3), stale (22d > 21d threshold)
        var tasks = Enumerable.Range(0, 4).Select(_ => ActiveTask(7, activatedDaysAgo: 22)).ToList();

        var (signals, _) = CreateCalculator().Compute(engineer, tasks, null);

        signals.TrippedCount.Should().Be(3);
    }

    // ── Department threshold override ───────────────────────────────────────────

    [Fact]
    public void Department_override_replaces_the_global_ratio_for_the_load_signal()
    {
        // Global ratio 1.3 -> threshold 26 (would trip at 27 pts); department override raises the
        // ratio to 2.0 -> threshold 40, so the same 27 pts should no longer trip.
        var engineer = NewEngineer(baselinePoints: 20);
        var tasks = ActiveTasks(27);
        var deptOverride = new DepartmentThresholdOverride { Department = "Core Banking", LoadVsBaselineRatio = 2.0 };

        var (signals, _) = CreateCalculator().Compute(engineer, tasks, null, deptOverride);

        signals.LoadVsBaseline.Tripped.Should().BeFalse();
    }

    [Fact]
    public void Department_override_does_not_affect_engineers_without_it()
    {
        var engineer = NewEngineer(baselinePoints: 20);
        var tasks = ActiveTasks(27);

        var (signals, _) = CreateCalculator().Compute(engineer, tasks, null, departmentOverride: null);

        signals.LoadVsBaseline.Tripped.Should().BeTrue();
    }

    [Fact]
    public void Department_override_unset_field_falls_back_to_the_global_default()
    {
        // Override only raises MaxConcurrentTasks; load-signal math must still use the global ratio.
        var engineer = NewEngineer(baselinePoints: 20);
        var tasks = ActiveTasks(27);
        var deptOverride = new DepartmentThresholdOverride { Department = "Core Banking", MaxConcurrentTasks = 10 };

        var (signals, _) = CreateCalculator().Compute(engineer, tasks, null, deptOverride);

        signals.LoadVsBaseline.Tripped.Should().BeTrue("the load ratio wasn't overridden, so the global default still applies");
    }
}
