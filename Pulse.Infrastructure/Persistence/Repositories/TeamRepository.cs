using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Teams;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.Persistence.Repositories;

public class TeamRepository : ITeamRepository
{
    private readonly PulseDbContext _db;

    public TeamRepository(PulseDbContext db) => _db = db;

    public Task<Team?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _db.Teams.FirstOrDefaultAsync(t => t.Id == id, ct);

    public async Task<IReadOnlyList<Team>> ListAllAsync(CancellationToken ct = default) =>
        await _db.Teams.OrderBy(t => t.Name).ToListAsync(ct);

    public async Task AddAsync(Team team, CancellationToken ct = default) =>
        await _db.Teams.AddAsync(team, ct);

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        _db.SaveChangesAsync(ct);
}
