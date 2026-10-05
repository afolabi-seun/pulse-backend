using Pulse.Application.Common.Interfaces;
using Pulse.Application.Performance;
using FluentAssertions;

namespace Pulse.UnitTests.Performance;

public class PerformanceMetricsCalculatorTests
{
    private static readonly Guid EngineerId = Guid.NewGuid();
    private static readonly DateOnly From = new(2026, 1, 5);  // Monday
    private static readonly DateOnly To = new(2026, 1, 16);   // Friday, 2 full weeks later

    [Fact]
    public void VelocityRatio_compares_delivered_points_with_what_the_baseline_expects_over_the_window()
    {
        // 20 pts per 5 days over a 30-day window expects 120 points; 30 delivered is 25%, not 150%.
        var stats = new TaskPerformanceStats(30, 0, 0, 0, null, 0, 0);

        var dto = PerformanceMetricsCalculator.Compute(EngineerId, "Dev", 20, 5, stats, 0, 0, From, From.AddDays(30));

        dto.ExpectedPoints.Should().Be(120);
        dto.VelocityRatio.Should().Be(0.25);
    }

    [Fact]
    public void VelocityRatio_is_100_percent_when_delivering_exactly_the_baseline_rate()
    {
        // 21 pts per 7 days over 28 days expects 84.
        var stats = new TaskPerformanceStats(84, 0, 0, 0, null, 0, 0);

        var dto = PerformanceMetricsCalculator.Compute(EngineerId, "Dev", 21, 7, stats, 0, 0, From, From.AddDays(28));

        dto.VelocityRatio.Should().Be(1.0);
    }

    [Fact]
    public void A_longer_window_expects_proportionally_more_so_the_same_output_scores_lower()
    {
        var stats = new TaskPerformanceStats(30, 0, 0, 0, null, 0, 0);

        var thirty = PerformanceMetricsCalculator.Compute(EngineerId, "Dev", 20, 5, stats, 0, 0, From, From.AddDays(30));
        var ninety = PerformanceMetricsCalculator.Compute(EngineerId, "Dev", 20, 5, stats, 0, 0, From, From.AddDays(90));

        ninety.ExpectedPoints.Should().Be(thirty.ExpectedPoints * 3);
        ninety.VelocityRatio.Should().BeApproximately(thirty.VelocityRatio / 3, 1e-9);
    }

    [Fact]
    public void Exposes_the_cycle_length_the_expectation_was_based_on()
    {
        var stats = new TaskPerformanceStats(0, 0, 0, 0, null, 0, 0);

        var dto = PerformanceMetricsCalculator.Compute(EngineerId, "Dev", 12, 3, stats, 0, 0, From, From.AddDays(30));

        dto.BaselinePoints.Should().Be(12);
        dto.BaselineCycleDays.Should().Be(3);
    }

    [Fact]
    public void VelocityRatio_is_zero_when_baseline_is_zero()
    {
        var stats = new TaskPerformanceStats(30, 0, 0, 0, null, 0, 0);

        var dto = PerformanceMetricsCalculator.Compute(EngineerId, "Dev", 0, 5, stats, 0, 0, From, To);

        dto.VelocityRatio.Should().Be(0);
        dto.ExpectedPoints.Should().Be(0);
    }

    [Fact]
    public void VelocityRatio_is_zero_when_the_cycle_length_is_missing()
    {
        var stats = new TaskPerformanceStats(30, 0, 0, 0, null, 0, 0);

        var dto = PerformanceMetricsCalculator.Compute(EngineerId, "Dev", 20, 0, stats, 0, 0, From, To);

        dto.VelocityRatio.Should().Be(0);
        dto.ExpectedPoints.Should().Be(0);
    }

    [Fact]
    public void OnTimeRate_is_null_when_nothing_completed()
    {
        var stats = new TaskPerformanceStats(0, 0, 0, 0, null, 0, 0);

        var dto = PerformanceMetricsCalculator.Compute(EngineerId, "Dev", 20, 5, stats, 0, 0, From, To);

        dto.OnTimeRate.Should().BeNull();
    }

    [Fact]
    public void OnTimeRate_divides_on_time_by_tasks_with_a_due_date()
    {
        var stats = new TaskPerformanceStats(10, 4, 4, 3, null, 0, 0);

        var dto = PerformanceMetricsCalculator.Compute(EngineerId, "Dev", 20, 5, stats, 0, 0, From, To);

        dto.OnTimeRate.Should().Be(0.75);
    }

    [Fact]
    public void OnTimeRate_is_null_when_completed_tasks_had_no_due_date_at_all()
    {
        // 4 tasks completed, none of them ever had a due date — there's no punctuality to measure,
        // so this must read as N/A rather than a fabricated 0% (nobody was ever late) or 100%
        // (nobody missed a deadline that didn't exist).
        var stats = new TaskPerformanceStats(10, 4, TasksWithDueDate: 0, TasksCompletedOnTime: 0, null, 0, 0);

        var dto = PerformanceMetricsCalculator.Compute(EngineerId, "Dev", 20, 5, stats, 0, 0, From, To);

        dto.OnTimeRate.Should().BeNull();
    }

    [Fact]
    public void OnTimeRate_excludes_undated_tasks_from_both_sides_of_the_ratio()
    {
        // 5 completed, only 2 ever had a due date, both met — must be 100%, not 2/5 (40%), which
        // would count the 3 undated tasks as if they'd missed a deadline they never had.
        var stats = new TaskPerformanceStats(10, 5, TasksWithDueDate: 2, TasksCompletedOnTime: 2, null, 0, 0);

        var dto = PerformanceMetricsCalculator.Compute(EngineerId, "Dev", 20, 5, stats, 0, 0, From, To);

        dto.OnTimeRate.Should().Be(1.0);
    }

    [Fact]
    public void QaRejectRate_is_null_when_nothing_sent_to_qa()
    {
        var stats = new TaskPerformanceStats(0, 0, 0, 0, null, 0, 0);

        var dto = PerformanceMetricsCalculator.Compute(EngineerId, "Dev", 20, 5, stats, 0, 0, From, To);

        dto.QaRejectRate.Should().BeNull();
    }

    [Fact]
    public void QaRejectRate_divides_rejected_by_sent()
    {
        var stats = new TaskPerformanceStats(0, 0, 0, 0, null, 5, 1);

        var dto = PerformanceMetricsCalculator.Compute(EngineerId, "Dev", 20, 5, stats, 0, 0, From, To);

        dto.QaRejectRate.Should().Be(0.2);
    }

    [Fact]
    public void CheckInConsistency_divides_checkins_by_weekdays_in_range()
    {
        // From (Mon 2026-01-05) to To (Fri 2026-01-16) spans 10 weekdays.
        var stats = new TaskPerformanceStats(0, 0, 0, 0, null, 0, 0);

        var dto = PerformanceMetricsCalculator.Compute(EngineerId, "Dev", 20, 5, stats, 0, checkInCount: 8, From, To);

        dto.ExpectedCheckInDays.Should().Be(10);
        dto.CheckInConsistency.Should().Be(0.8);
    }

    [Fact]
    public void CheckInConsistency_is_capped_at_one_even_with_extra_checkins()
    {
        var stats = new TaskPerformanceStats(0, 0, 0, 0, null, 0, 0);

        var dto = PerformanceMetricsCalculator.Compute(EngineerId, "Dev", 20, 5, stats, 0, checkInCount: 999, From, To);

        dto.CheckInConsistency.Should().Be(1.0);
    }

    [Fact]
    public void EscalatedTaskCount_and_AvgCycleTimeDays_pass_through_unchanged()
    {
        var stats = new TaskPerformanceStats(0, 2, 2, 1, 3.5, 0, 0);

        var dto = PerformanceMetricsCalculator.Compute(EngineerId, "Dev", 20, 5, stats, 4, 0, From, To);

        dto.EscalatedTaskCount.Should().Be(4);
        dto.AvgCycleTimeDays.Should().Be(3.5);
    }
    [Fact]
    public void CycleTimeDays_counts_whole_calendar_days_from_creation_to_the_end_date()
    {
        var created = new DateTime(2026, 1, 5, 15, 0, 0, DateTimeKind.Utc);

        PerformanceMetricsCalculator.CycleTimeDays(created, new DateOnly(2026, 1, 12), new DateTime(2026, 1, 20))
            .Should().Be(7);
    }

    [Fact]
    public void CycleTimeDays_is_zero_for_a_task_finished_the_day_it_was_created()
    {
        // Created at 3pm, ended "today": the end date is a date, so a naive subtraction gives -0.6 days.
        var created = new DateTime(2026, 1, 5, 15, 0, 0, DateTimeKind.Utc);

        PerformanceMetricsCalculator.CycleTimeDays(created, new DateOnly(2026, 1, 5), new DateTime(2026, 1, 5, 16, 0, 0))
            .Should().Be(0);
    }

    [Fact]
    public void CycleTimeDays_is_never_negative_when_the_end_date_precedes_creation()
    {
        var created = new DateTime(2026, 1, 10, 9, 0, 0, DateTimeKind.Utc);

        PerformanceMetricsCalculator.CycleTimeDays(created, new DateOnly(2026, 1, 5), new DateTime(2026, 1, 11))
            .Should().Be(0);
    }

    [Fact]
    public void CycleTimeDays_falls_back_to_the_day_it_became_done_when_there_is_no_end_date()
    {
        var created = new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc);

        PerformanceMetricsCalculator.CycleTimeDays(created, null, new DateTime(2026, 1, 9, 17, 0, 0, DateTimeKind.Utc))
            .Should().Be(4);
    }
}
