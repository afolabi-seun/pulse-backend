using Pulse.Application.Auth;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Alerts;

namespace Pulse.Application.Alerts;

public interface IAlertMetricsProvider
{
    Task<double> GetCurrentValueAsync(AlertMetric metric, AlertScopeType scopeType, Guid scopeId, CancellationToken ct = default);
}

/// <summary>Computes an AlertRule's current metric value fresh on every call — deliberately not
/// shared with the PMO/Weekly report handlers' own inline computations (those are entangled with
/// each report's own department/PMO scoping and aren't cleanly extractable without touching
/// well-exercised, live report code). The tradeoff: this and the reports could in principle drift
/// on how a number is computed. Each method here stays intentionally simple and reads directly off
/// the same repositories the reports use, to keep that drift risk as low as practical without a
/// full extraction.</summary>
public class AlertMetricsProvider : IAlertMetricsProvider
{
    private readonly ITaskRepository _tasks;
    private readonly IEngineerRepository _engineers;
    private readonly ICheckInRepository _checkIns;
    private readonly IProjectRepository _projects;
    private readonly IAlertMetricSnapshotRepository _snapshots;

    // A "percent change" variant compares against the closest snapshot at or before this far back —
    // not exactly 7 days, since the scanner only records a snapshot when it happens to run.
    private static readonly TimeSpan ChangeWindow = TimeSpan.FromDays(7);

    public AlertMetricsProvider(
        ITaskRepository tasks, IEngineerRepository engineers, ICheckInRepository checkIns,
        IProjectRepository projects, IAlertMetricSnapshotRepository snapshots)
    {
        _tasks = tasks;
        _engineers = engineers;
        _checkIns = checkIns;
        _projects = projects;
        _snapshots = snapshots;
    }

    public Task<double> GetCurrentValueAsync(AlertMetric metric, AlertScopeType scopeType, Guid scopeId, CancellationToken ct = default) =>
        metric switch
        {
            AlertMetric.BlockerCount => GetBlockerCountAsync(scopeType, scopeId, ct),
            AlertMetric.TeamVelocity => GetTeamVelocityAsync(scopeId, ct),
            AlertMetric.CheckInCompliance => GetCheckInComplianceAsync(scopeType, scopeId, ct),
            AlertMetric.QaRejectRate => GetQaRejectRateAsync(scopeType, scopeId, ct),
            AlertMetric.TeamVelocityChange => GetTeamVelocityChangeAsync(scopeId, ct),
            AlertMetric.BlockerCountChange => GetChangeAsync(AlertMetric.BlockerCount, scopeType, scopeId,
                () => GetBlockerCountAsync(scopeType, scopeId, ct), ct),
            AlertMetric.CheckInComplianceChange => GetChangeAsync(AlertMetric.CheckInCompliance, scopeType, scopeId,
                () => GetCheckInComplianceAsync(scopeType, scopeId, ct), ct),
            AlertMetric.QaRejectRateChange => GetChangeAsync(AlertMetric.QaRejectRate, scopeType, scopeId,
                () => GetQaRejectRateAsync(scopeType, scopeId, ct), ct),
            _ => throw new ArgumentOutOfRangeException(nameof(metric)),
        };

    /// <summary>Shared by every snapshot-backed "Change" metric: compute the base metric's current
    /// value (which snapshots it as a side effect — see each base method), then diff against the
    /// closest snapshot from ~7 days ago. Zero when there's no snapshot that old yet, or when that
    /// snapshot's value was zero — same "not enough history" / "undefined against zero" edge cases
    /// TeamVelocityChange already uses, kept consistent on purpose.</summary>
    private async Task<double> GetChangeAsync(
        AlertMetric baseMetric, AlertScopeType scopeType, Guid scopeId, Func<Task<double>> getCurrent, CancellationToken ct)
    {
        var current = await getCurrent();
        var previous = await _snapshots.GetMostRecentBeforeAsync(baseMetric, scopeType, scopeId, DateTime.UtcNow - ChangeWindow, ct);
        if (previous is null || previous.Value == 0) return 0;

        return Math.Round((current - previous.Value) / previous.Value * 100, 1);
    }

    private async Task RecordSnapshotAsync(AlertMetric metric, AlertScopeType scopeType, Guid scopeId, double value, CancellationToken ct)
    {
        await _snapshots.RecordAsync(AlertMetricSnapshot.Create(metric, scopeType, scopeId, value), ct);
        await _snapshots.SaveChangesAsync(ct);
    }

    private async Task<double> GetBlockerCountAsync(AlertScopeType scopeType, Guid scopeId, CancellationToken ct)
    {
        var blocked = await _tasks.GetBlockedTasksAsync(ct);
        double value;
        if (scopeType == AlertScopeType.Project)
        {
            value = blocked.Count(t => t.ProjectId == scopeId);
        }
        else
        {
            var teamEngineerIds = (await _engineers.ListAllAsync(ct))
                .Where(e => e.TeamId == scopeId)
                .Select(e => e.Id)
                .ToHashSet();
            value = blocked.Count(t => t.AssigneeId.HasValue && teamEngineerIds.Contains(t.AssigneeId.Value));
        }

        await RecordSnapshotAsync(AlertMetric.BlockerCount, scopeType, scopeId, value, ct);
        return value;
    }

    private async Task<double> GetTeamVelocityAsync(Guid teamId, CancellationToken ct)
    {
        var points = await _tasks.GetWeeklyThroughputByTeamAsync(teamId, ct);
        // The 6-week rolling window's latest entry is the current, in-progress week.
        return points.OrderByDescending(p => p.WeekOf).FirstOrDefault()?.PointsDelivered ?? 0;
    }

    private async Task<double> GetTeamVelocityChangeAsync(Guid teamId, CancellationToken ct)
    {
        var ordered = (await _tasks.GetWeeklyThroughputByTeamAsync(teamId, ct))
            .OrderByDescending(p => p.WeekOf)
            .ToList();
        // Not enough history yet to compare — reads as "no change" rather than a fabricated spike.
        if (ordered.Count < 2) return 0;

        var current = ordered[0].PointsDelivered;
        var previous = ordered[1].PointsDelivered;
        // A percent change against a zero prior week is mathematically undefined — treated the same
        // as "not enough history" rather than picking an arbitrary large number.
        if (previous == 0) return 0;

        return Math.Round((double)(current - previous) / previous * 100, 1);
    }

    private async Task<double> GetCheckInComplianceAsync(AlertScopeType scopeType, Guid teamId, CancellationToken ct)
    {
        var roster = (await _engineers.ListAllAsync(ct))
            .Where(e => e.TeamId == teamId && e.IsActive
                && CapabilityRegistry.All[CapabilityRegistry.CheckInExpected].AllowedRoles.Contains(e.Role))
            .ToList();
        double value;
        if (roster.Count == 0)
        {
            // Nobody on this team is expected to check in at all — nothing to flag as non-compliant.
            value = 100;
        }
        else
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var weekStart = today.AddDays(-((int)today.DayOfWeek + 6) % 7);
            var counts = await _checkIns.GetCheckInCountByDateRangeAsync(weekStart, today, ct);
            var checkedIn = roster.Count(e => counts.TryGetValue(e.Id, out var c) && c > 0);
            value = Math.Round((double)checkedIn / roster.Count * 100, 1);
        }

        await RecordSnapshotAsync(AlertMetric.CheckInCompliance, scopeType, teamId, value, ct);
        return value;
    }

    private async Task<double> GetQaRejectRateAsync(AlertScopeType scopeType, Guid projectId, CancellationToken ct)
    {
        var members = await _projects.ListMembersAsync(projectId, ct);
        var to = DateTime.UtcNow;
        var from = to.AddDays(-30);

        var sent = 0;
        var rejected = 0;
        foreach (var member in members)
        {
            var stats = await _tasks.GetPerformanceStatsAsync(member.EngineerId, from, to, projectId, ct);
            sent += stats.TasksSentToQa;
            rejected += stats.TasksQaRejected;
        }
        var value = sent > 0 ? Math.Round((double)rejected / sent * 100, 1) : 0;

        await RecordSnapshotAsync(AlertMetric.QaRejectRate, scopeType, projectId, value, ct);
        return value;
    }
}
