using Pulse.Application.Common.Interfaces;

namespace Pulse.Application.Performance;

/// <summary>Pure ratio math over already-fetched raw stats — mirrors OverworkSignalsCalculator's shape.</summary>
public static class PerformanceMetricsCalculator
{
    public static PerformanceMetricsDto Compute(
        Guid engineerId, string engineerName, int baselinePoints, int baselineCycleDays,
        TaskPerformanceStats stats, int escalatedTaskCount, int checkInCount,
        DateOnly from, DateOnly to)
    {
        // BaselinePoints is a rate — "N points per BaselineCycleDays" — so what an engineer is expected to
        // deliver depends on how long the window is. Comparing a 30- or 90-day window's output against a
        // single cycle's points (as this once did) overstated velocity many times over. Calendar days, to
        // match the weekly baseline on the engineer page and the overwork signal's cycle window.
        var windowDays = Math.Max(to.DayNumber - from.DayNumber, 1);
        var expectedPoints = baselinePoints > 0 && baselineCycleDays > 0
            ? (double)baselinePoints * windowDays / baselineCycleDays
            : 0;
        var velocityRatio = expectedPoints > 0 ? stats.DeliveredPoints / expectedPoints : 0;
        // Denominator is tasks that actually had a due date, not every completed task — a task
        // never given a deadline has no "on time" to measure (see TaskPerformanceStats' own doc
        // comment), so it's excluded rather than counted as trivially on-time.
        double? onTimeRate = stats.TasksWithDueDate > 0
            ? (double)stats.TasksCompletedOnTime / stats.TasksWithDueDate
            : null;
        double? qaRejectRate = stats.TasksSentToQa > 0
            ? (double)stats.TasksQaRejected / stats.TasksSentToQa
            : null;

        var expectedCheckInDays = CountWeekdays(from, to);
        var checkInConsistency = expectedCheckInDays > 0
            ? Math.Min((double)checkInCount / expectedCheckInDays, 1.0)
            : 0;

        return new PerformanceMetricsDto(
            engineerId, engineerName, from, to,
            stats.DeliveredPoints, baselinePoints, baselineCycleDays, expectedPoints, velocityRatio,
            stats.TasksCompleted, stats.TasksWithDueDate, stats.TasksCompletedOnTime, onTimeRate,
            stats.AvgCycleTimeDays,
            stats.TasksSentToQa, stats.TasksQaRejected, qaRejectRate,
            escalatedTaskCount,
            checkInCount, expectedCheckInDays, checkInConsistency);
    }

    /// <summary>Whole days from a task being created to it being finished, never negative. The finish is the
    /// task's ActualEndDate when it has one (a date, so the comparison is by calendar day: a task created at
    /// 3pm and finished the same day is 0 days, not a fraction below zero) and otherwise the day it moved to
    /// Done. A finish that precedes creation — an end date edited to before the task existed, or imported
    /// data — reads as 0 rather than dragging the average negative.</summary>
    public static int CycleTimeDays(DateTime createdAt, DateOnly? actualEndDate, DateTime completedAt)
    {
        var finished = actualEndDate ?? DateOnly.FromDateTime(completedAt);
        return Math.Max(0, finished.DayNumber - DateOnly.FromDateTime(createdAt).DayNumber);
    }

    private static int CountWeekdays(DateOnly from, DateOnly to)
    {
        var count = 0;
        for (var d = from; d <= to; d = d.AddDays(1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                count++;
        return count;
    }
}
