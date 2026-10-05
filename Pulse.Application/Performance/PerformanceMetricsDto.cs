namespace Pulse.Application.Performance;

public record PerformanceMetricsDto(
    Guid EngineerId,
    string EngineerName,
    DateOnly From,
    DateOnly To,
    int DeliveredPoints,
    int BaselinePoints,
    int BaselineCycleDays,
    // What the baseline implies for this window: BaselinePoints x window days / BaselineCycleDays.
    // VelocityRatio is DeliveredPoints / ExpectedPoints (zero when there is no usable baseline).
    double ExpectedPoints,
    double VelocityRatio,
    int TasksCompleted,
    // Of TasksCompleted, how many actually had a due date — the real denominator for OnTimeRate.
    // A task with no due date has no "on time" to measure, so it's excluded from both sides.
    int TasksWithDueDate,
    int TasksCompletedOnTime,
    double? OnTimeRate,
    double? AvgCycleTimeDays,
    int TasksSentToQa,
    int TasksQaRejected,
    double? QaRejectRate,
    int EscalatedTaskCount,
    int CheckInCount,
    int ExpectedCheckInDays,
    double CheckInConsistency);
