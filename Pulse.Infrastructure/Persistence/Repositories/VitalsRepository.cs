using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Vitals;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.Persistence.Repositories;

public class VitalsRepository : IVitalsRepository
{
    private readonly PulseDbContext _db;

    public VitalsRepository(PulseDbContext db) => _db = db;

    public async Task<IReadOnlyList<VitalsResponse>> ListAsync(DateOnly? weekOf, CancellationToken ct = default)
    {
        var query = _db.VitalsResponses.AsQueryable();
        if (weekOf.HasValue)
            query = query.Where(p => p.WeekOf == weekOf.Value);
        return await query.OrderByDescending(p => p.WeekOf).ThenByDescending(p => p.CreatedAt).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<VitalsResponse>> ListByDepartmentAsync(string department, DateOnly? weekOf, CancellationToken ct = default)
    {
        var deptEngineerIds = _db.Engineers
            .Where(e => e.TeamId != null && _db.Teams.Any(t => t.Id == e.TeamId && t.Department == department))
            .Select(e => e.Id);

        var query = _db.VitalsResponses.Where(p => deptEngineerIds.Contains(p.EngineerId));
        if (weekOf.HasValue)
            query = query.Where(p => p.WeekOf == weekOf.Value);

        return await query.OrderByDescending(p => p.WeekOf).ThenByDescending(p => p.CreatedAt).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<VitalsResponse>> ListByEngineerAsync(Guid engineerId, CancellationToken ct = default) =>
        await _db.VitalsResponses
            .Where(p => p.EngineerId == engineerId)
            .OrderByDescending(p => p.WeekOf)
            .ToListAsync(ct);

    public async Task<VitalsResponse?> GetByEngineerAndWeekAsync(Guid engineerId, DateOnly weekOf, CancellationToken ct = default) =>
        await _db.VitalsResponses.FirstOrDefaultAsync(p => p.EngineerId == engineerId && p.WeekOf == weekOf, ct);

    public async Task<double?> GetAverageScoreAsync(DateOnly weekOf, CancellationToken ct = default) =>
        await _db.VitalsResponses.Where(p => p.WeekOf == weekOf).AverageAsync(p => (double?)p.Score, ct);

    public async Task<int> CountDistinctSourcesAsync(DateOnly weekOf, CancellationToken ct = default) =>
        await _db.VitalsResponses.Where(p => p.WeekOf == weekOf).Select(p => p.EngineerId).Distinct().CountAsync(ct);

    public async Task<VitalsResponse?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.VitalsResponses.FindAsync([id], ct);

    public async Task AddAsync(VitalsResponse response, CancellationToken ct = default) =>
        await _db.VitalsResponses.AddAsync(response, ct);

    public Task DeleteAsync(VitalsResponse response, CancellationToken ct = default)
    {
        _db.VitalsResponses.Remove(response);
        return Task.CompletedTask;
    }

    public async Task SaveChangesAsync(CancellationToken ct = default) =>
        await _db.SaveChangesAsync(ct);
}
