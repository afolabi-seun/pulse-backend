namespace Pulse.Application.Overwork;

public record OverworkSignalDto(bool Tripped, string Reason);

/// <summary>The numbers behind the load signal, for showing alongside the baseline: how much is active in total, how
/// much of it is due this cycle (what the signal tests), and the point above which it trips.</summary>
public record WorkloadDto(int ActivePoints, int CyclePoints, int CycleDays, double ThresholdPoints);

public record OverworkSignalsDto(
    OverworkSignalDto LoadVsBaseline,
    OverworkSignalDto Concurrent,
    OverworkSignalDto StaleInProgress,
    bool IsOverworked,
    bool HasActiveOverride,
    WorkloadDto? Workload = null)
{
    public static OverworkSignalsDto From(OverworkSignals signals, bool isOverworked, bool hasActiveOverride) =>
        new(
            new(signals.LoadVsBaseline.Tripped, signals.LoadVsBaseline.Reason),
            new(signals.Concurrent.Tripped, signals.Concurrent.Reason),
            new(signals.StaleInProgress.Tripped, signals.StaleInProgress.Reason),
            isOverworked,
            hasActiveOverride,
            new WorkloadDto(signals.ActivePoints, signals.CyclePoints, signals.CycleDays, signals.LoadThresholdPoints));
}
