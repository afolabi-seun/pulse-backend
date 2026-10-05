using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Alerts;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.Persistence.Repositories;

public class AlertMetricSnapshotRepository : IAlertMetricSnapshotRepository
{
    private readonly PulseDbContext _db;

    public AlertMetricSnapshotRepository(PulseDbContext db) => _db = db;

    public async Task RecordAsync(AlertMetricSnapshot snapshot, CancellationToken ct = default) =>
        await _db.AlertMetricSnapshots.AddAsync(snapshot, ct);

    public async Task<AlertMetricSnapshot?> GetMostRecentBeforeAsync(
        AlertMetric metric, AlertScopeType scopeType, Guid scopeId, DateTime cutoff, CancellationToken ct = default) =>
        await _db.AlertMetricSnapshots
            .Where(s => s.Metric == metric && s.ScopeType == scopeType && s.ScopeId == scopeId && s.CreatedAt <= cutoff)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}
