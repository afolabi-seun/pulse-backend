using Pulse.Domain.Common;

namespace Pulse.Domain.Alerts;

/// <summary>A point-in-time recording of what an AlertMetric was worth for a given scope, written as
/// a side effect of AlertMetricsProvider computing it — never on its own schedule. Exists so a
/// "percent change" variant of a point-in-time metric (BlockerCount, CheckInCompliance, QaRejectRate)
/// has something to diff against; TeamVelocityChange needs none of this because its source data
/// (GetWeeklyThroughputByTeamAsync) already keeps a rolling window. Uses the base Entity's CreatedAt
/// as its own recorded-at timestamp rather than a duplicate field.</summary>
public class AlertMetricSnapshot : Entity
{
    public AlertMetric Metric { get; private set; }
    public AlertScopeType ScopeType { get; private set; }
    public Guid ScopeId { get; private set; }
    public double Value { get; private set; }

    private AlertMetricSnapshot() { }

    public static AlertMetricSnapshot Create(AlertMetric metric, AlertScopeType scopeType, Guid scopeId, double value) => new()
    {
        Metric = metric,
        ScopeType = scopeType,
        ScopeId = scopeId,
        Value = value,
    };
}
