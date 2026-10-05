using Pulse.Domain.Alerts;

namespace Pulse.Application.Common.Interfaces;

public interface IAlertMetricSnapshotRepository
{
    Task RecordAsync(AlertMetricSnapshot snapshot, CancellationToken ct = default);

    /// <summary>The most recently recorded snapshot at or before <paramref name="cutoff"/> — the
    /// closest available history for a "percent change" comparison, not necessarily exactly on the
    /// cutoff date. Null if nothing was ever recorded that far back yet.</summary>
    Task<AlertMetricSnapshot?> GetMostRecentBeforeAsync(
        AlertMetric metric, AlertScopeType scopeType, Guid scopeId, DateTime cutoff, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
