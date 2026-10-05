using Pulse.Application.Escalations;
using Pulse.Application.Overwork;
using Pulse.Domain.Escalations;
using FluentAssertions;

namespace Pulse.UnitTests.Escalations;

public class EscalationLevelCalculatorTests
{
    // Pinned rather than DateTime.UtcNow: dueDate is a DateOnly reconstructed at end-of-day, so the
    // gap between "now" and "due" silently varies with whatever time-of-day the suite happens to run
    // at (anywhere from ~1 to ~2 days for an AddDays(1) due date) — enough drift to flip a tight 0.85
    // elapsed-pct threshold. A fixed reference makes every test's totalDays/elapsedPct exact.
    private static readonly DateTime Now = new(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);

    private readonly OverworkThresholds _thresholds = new();

    [Fact]
    public void Returns_null_when_task_just_activated_with_plenty_of_time_left()
    {
        var dueDate = DateOnly.FromDateTime(Now.AddDays(20));
        var activatedAt = Now;

        var level = EscalationLevelCalculator.Determine(dueDate, activatedAt, _thresholds, Now);

        level.Should().BeNull();
    }

    [Fact]
    public void Returns_TMinus3_once_past_the_elapsed_threshold()
    {
        // totalDays ≈ 10 (due in 3 days, activated 7 days ago). Widening the day-based thresholds
        // (well above totalDays * (1 - flat%)) makes the flat ElapsedPct% the dominant term, so the
        // test only depends on "is elapsedPct >= 0.60", not on exact day-boundary arithmetic.
        _thresholds.EscalationT3Days = 5;
        _thresholds.EscalationT1Days = 5;
        var dueDate = DateOnly.FromDateTime(Now.AddDays(3));
        var activatedAt = Now.AddDays(-7);

        var level = EscalationLevelCalculator.Determine(dueDate, activatedAt, _thresholds, Now);

        level.Should().Be(EscalationLevel.TMinus3);
    }

    [Fact]
    public void Returns_TMinus1_once_past_the_tighter_elapsed_threshold()
    {
        // totalDays ≈ 10 (due in 1 day, activated 9 days ago), same day-threshold widening as above,
        // this time isolating the flat 0.85 T-1 threshold.
        _thresholds.EscalationT3Days = 5;
        _thresholds.EscalationT1Days = 5;
        var dueDate = DateOnly.FromDateTime(Now.AddDays(1));
        var activatedAt = Now.AddDays(-9);

        var level = EscalationLevelCalculator.Determine(dueDate, activatedAt, _thresholds, Now);

        level.Should().Be(EscalationLevel.TMinus1);
    }

    [Fact]
    public void Returns_Overdue_when_due_date_has_passed()
    {
        var dueDate = DateOnly.FromDateTime(Now.AddDays(-1));
        var activatedAt = Now.AddDays(-5);

        var level = EscalationLevelCalculator.Determine(dueDate, activatedAt, _thresholds, Now);

        level.Should().Be(EscalationLevel.Overdue);
    }

    [Fact]
    public void Returns_Overdue_for_zero_or_negative_duration_even_if_due_date_is_still_ahead()
    {
        // ActivatedAt after the due date — a data anomaly (e.g. a reactivated task whose due date
        // wasn't pushed out) — should still resolve to Overdue rather than silently not escalating.
        var dueDate = DateOnly.FromDateTime(Now.AddDays(1));
        var activatedAt = Now.AddDays(3);

        var level = EscalationLevelCalculator.Determine(dueDate, activatedAt, _thresholds, Now);

        level.Should().Be(EscalationLevel.Overdue);
    }

    [Fact]
    public void Returns_null_within_the_grace_window_for_a_just_reactivated_overdue_task()
    {
        // The due date already passed before the task reactivated (e.g. sent back from a QA
        // rejection after sitting in QA past its deadline) — the fresh ActivatedAt should buy it
        // a grace window instead of immediately firing the full Overdue broadcast.
        var dueDate = DateOnly.FromDateTime(Now.AddDays(-10));
        var activatedAt = Now.AddMinutes(-5);

        var level = EscalationLevelCalculator.Determine(dueDate, activatedAt, _thresholds, Now);

        level.Should().BeNull();
    }

    [Fact]
    public void Returns_Overdue_once_the_grace_window_after_reactivation_expires()
    {
        var dueDate = DateOnly.FromDateTime(Now.AddDays(-10));
        var activatedAt = Now.AddHours(-(_thresholds.EscalationReactivationGraceHours + 1));

        var level = EscalationLevelCalculator.Determine(dueDate, activatedAt, _thresholds, Now);

        level.Should().Be(EscalationLevel.Overdue);
    }
}
