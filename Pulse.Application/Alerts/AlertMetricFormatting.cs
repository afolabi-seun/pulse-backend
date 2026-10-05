using Pulse.Domain.Alerts;

namespace Pulse.Application.Alerts;

/// <summary>Shared display formatting for an AlertMetric's value — used by both AlertRuleScanner's
/// templated messages and AnthropicAlertExplainer's prompts, so the two never drift on how a number
/// is described to a person.</summary>
public static class AlertMetricFormatting
{
    public static string MetricLabel(AlertMetric metric) => metric switch
    {
        AlertMetric.BlockerCount => "blocker count",
        AlertMetric.TeamVelocity => "velocity this week",
        AlertMetric.CheckInCompliance => "check-in compliance",
        AlertMetric.QaRejectRate => "QA reject rate",
        AlertMetric.TeamVelocityChange => "week-over-week velocity change",
        AlertMetric.BlockerCountChange => "week-over-week blocker count change",
        AlertMetric.CheckInComplianceChange => "week-over-week check-in compliance change",
        AlertMetric.QaRejectRateChange => "week-over-week QA reject rate change",
        _ => metric.ToString(),
    };

    private static readonly HashSet<AlertMetric> ChangeMetrics = new()
    {
        AlertMetric.TeamVelocityChange, AlertMetric.BlockerCountChange,
        AlertMetric.CheckInComplianceChange, AlertMetric.QaRejectRateChange,
    };

    // Every "Change" metric's value is a signed percentage — "+12%"/"-42%" reads far more clearly
    // than a bare "12" the way every other metric's raw count/percentage does.
    public static string FormatValue(AlertMetric metric, double value) =>
        ChangeMetrics.Contains(metric) ? $"{value:+0.##;-0.##;0}%" : $"{value:0.##}";
}
