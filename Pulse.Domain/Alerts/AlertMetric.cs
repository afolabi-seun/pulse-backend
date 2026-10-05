namespace Pulse.Domain.Alerts;

/// <summary>What an AlertRule watches. Each metric only makes sense against one AlertScopeType —
/// see AlertRule.ValidateScope. TeamVelocity/CheckInCompliance are Team-only because their source
/// data (ITaskRepository.GetWeeklyThroughputByTeamAsync, roster-based check-in coverage) is computed
/// per team, not per project; QaRejectRate is Project-only because QA review is a project-scoped
/// workflow. Overwork is deliberately not a metric here — OverworkDigestJob already proactively
/// alerts team leads/PMs/PMO on it, so a generic AlertRule would duplicate an existing, more
/// sophisticated pipeline (per-engineer baselines, department threshold overrides, active
/// overrides) rather than fill a real gap.</summary>
public enum AlertMetric
{
    /// <summary>Count of currently Blocked tasks in scope.</summary>
    BlockerCount,
    /// <summary>Story points delivered by the team in the current week (Monday-to-date).</summary>
    TeamVelocity,
    /// <summary>Percentage (0-100) of the team's check-in-expected roster who have checked in this week.</summary>
    CheckInCompliance,
    /// <summary>Percentage (0-100) of the project's QA submissions rejected, over the trailing 30 days.</summary>
    QaRejectRate,
    /// <summary>Percent change in the team's delivered points versus the prior week (can be negative).
    /// Reuses the same 6-week rolling window TeamVelocity reads, so it needs no new stored history —
    /// only Team scope has that data. 0 when there's no prior week to compare against yet, or when
    /// the prior week delivered 0 points (a percent change against zero is undefined).</summary>
    TeamVelocityChange,
    /// <summary>Percent change in blocker count versus ~7 days ago (can be negative — a drop in
    /// blockers). Unlike TeamVelocityChange, BlockerCount has no existing rolling-window source, so
    /// this reads its history from AlertMetricSnapshot instead — see AlertMetricsProvider. Same
    /// scope rule as BlockerCount (Team or Project); same zero-history/zero-prior-value edge cases
    /// as TeamVelocityChange.</summary>
    BlockerCountChange,
    /// <summary>Percent change in check-in compliance versus ~7 days ago. Team scope only, same as
    /// CheckInCompliance. Snapshot-backed — see BlockerCountChange.</summary>
    CheckInComplianceChange,
    /// <summary>Percent change in the trailing-30-day QA reject rate versus ~7 days ago. Project
    /// scope only, same as QaRejectRate. Snapshot-backed — see BlockerCountChange.</summary>
    QaRejectRateChange,
}
