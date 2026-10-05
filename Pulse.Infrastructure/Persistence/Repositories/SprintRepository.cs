using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Sprints;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.Persistence.Repositories;

public class SprintRepository : ISprintRepository
{
    private readonly PulseDbContext _db;

    public SprintRepository(PulseDbContext db) => _db = db;

    public Task<Sprint?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _db.Sprints.FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<IReadOnlyList<Sprint>> ListByTeamAsync(Guid? teamId, CancellationToken ct = default)
    {
        var query = _db.Sprints.AsQueryable();
        if (teamId.HasValue)
            query = query.Where(s => s.TeamId == teamId.Value);

        return await query
            .OrderByDescending(s => s.StartDate)
            .ToListAsync(ct);
    }

    public async Task AddAsync(Sprint sprint, CancellationToken ct = default) =>
        await _db.Sprints.AddAsync(sprint, ct);

    public Task RemoveAsync(Sprint sprint, CancellationToken ct = default)
    {
        _db.Sprints.Remove(sprint);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        _db.SaveChangesAsync(ct);
}
