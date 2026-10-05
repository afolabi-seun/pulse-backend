using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Automations;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.Persistence.Repositories;

public class AutomationRuleRepository : IAutomationRuleRepository
{
    private readonly PulseDbContext _db;

    public AutomationRuleRepository(PulseDbContext db) => _db = db;

    public async Task<AutomationRule?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.AutomationRules.FirstOrDefaultAsync(r => r.Id == id, ct);

    public async Task<IReadOnlyList<AutomationRule>> ListByOwnerAsync(Guid ownerEngineerId, CancellationToken ct = default) =>
        await _db.AutomationRules.Where(r => r.OwnerEngineerId == ownerEngineerId)
            .OrderByDescending(r => r.CreatedAt).ToListAsync(ct);

    public async Task<IReadOnlyList<AutomationRule>> ListActiveAsync(CancellationToken ct = default) =>
        await _db.AutomationRules.Where(r => r.IsActive).ToListAsync(ct);

    public async Task AddAsync(AutomationRule rule, CancellationToken ct = default) =>
        await _db.AutomationRules.AddAsync(rule, ct);

    public Task DeleteAsync(AutomationRule rule, CancellationToken ct = default)
    {
        _db.AutomationRules.Remove(rule);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}
