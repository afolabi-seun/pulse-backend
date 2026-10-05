using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Alerts;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.Persistence.Repositories;

public class AlertRuleRepository : IAlertRuleRepository
{
    private readonly PulseDbContext _db;

    public AlertRuleRepository(PulseDbContext db) => _db = db;

    public async Task<AlertRule?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.AlertRules.FirstOrDefaultAsync(r => r.Id == id, ct);

    public async Task<IReadOnlyList<AlertRule>> ListByOwnerAsync(Guid ownerEngineerId, CancellationToken ct = default) =>
        await _db.AlertRules.Where(r => r.OwnerEngineerId == ownerEngineerId)
            .OrderByDescending(r => r.CreatedAt).ToListAsync(ct);

    public async Task<IReadOnlyList<AlertRule>> ListActiveAsync(CancellationToken ct = default) =>
        await _db.AlertRules.Where(r => r.IsActive).ToListAsync(ct);

    public async Task AddAsync(AlertRule rule, CancellationToken ct = default) =>
        await _db.AlertRules.AddAsync(rule, ct);

    public Task DeleteAsync(AlertRule rule, CancellationToken ct = default)
    {
        _db.AlertRules.Remove(rule);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}
