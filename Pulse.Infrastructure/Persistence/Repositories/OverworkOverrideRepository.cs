using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Overrides;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.Persistence.Repositories;

public class OverworkOverrideRepository : IOverworkOverrideRepository
{
    private readonly PulseDbContext _db;

    public OverworkOverrideRepository(PulseDbContext db) => _db = db;

    public async Task<OverworkOverride?> GetActiveAsync(Guid engineerId, CancellationToken ct = default) =>
        await _db.OverworkOverrides
            .Where(o => o.EngineerId == engineerId && o.ExpiresAt > DateTime.UtcNow)
            .OrderByDescending(o => o.ExpiresAt)
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<OverworkOverride>> GetAllActiveAsync(CancellationToken ct = default) =>
        await _db.OverworkOverrides
            .Where(o => o.ExpiresAt > DateTime.UtcNow)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<OverworkOverride>> ListByEngineerAsync(Guid engineerId, CancellationToken ct = default) =>
        await _db.OverworkOverrides
            .Where(o => o.EngineerId == engineerId)
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync(ct);

    public async Task AddAsync(OverworkOverride @override, CancellationToken ct = default) =>
        await _db.OverworkOverrides.AddAsync(@override, ct);

    public async Task SaveChangesAsync(CancellationToken ct = default) =>
        await _db.SaveChangesAsync(ct);
}
