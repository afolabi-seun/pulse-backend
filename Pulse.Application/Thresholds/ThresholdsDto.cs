namespace Pulse.Application.Thresholds;

public record PointScaleEntryDto(int Value, string Label, string TimeGuide);

public record PriorityScaleEntryDto(int Value, string Label, string Criteria);

public record ThresholdsDto(
    double LoadVsBaselineRatio,
    int MaxConcurrentTasks,
    double StaleCycleMultiplier,
    int SignalsRequiredToFlag,
    double EscalationT3Days,
    double EscalationT3ElapsedPct,
    double EscalationT1Days,
    double EscalationT1ElapsedPct,
    double EscalationT3MinHours,
    double EscalationT1MinHours,
    int QaLeadTimeDays,
    IReadOnlyList<PointScaleEntryDto> PointScale,
    IReadOnlyList<PriorityScaleEntryDto> PriorityScale);
