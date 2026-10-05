namespace Pulse.Application.Overwork;

public record OverworkSignal(bool Tripped, string Reason);

public class OverworkSignals
{
    public required OverworkSignal LoadVsBaseline { get; init; }
    public required OverworkSignal Concurrent { get; init; }
    public required OverworkSignal StaleInProgress { get; init; }

    /// <summary>All of the engineer's active points, however far off they are due.</summary>
    public int ActivePoints { get; init; }
    /// <summary>The part of those due within the baseline cycle (plus overdue/undated) — what the load signal tests.</summary>
    public int CyclePoints { get; init; }
    public int CycleDays { get; init; }
    /// <summary>The load signal trips above this many cycle points (baseline × the department's ratio); 0 with no baseline.</summary>
    public double LoadThresholdPoints { get; init; }

    public int TrippedCount =>
        (LoadVsBaseline.Tripped ? 1 : 0) +
        (Concurrent.Tripped ? 1 : 0) +
        (StaleInProgress.Tripped ? 1 : 0);
}
