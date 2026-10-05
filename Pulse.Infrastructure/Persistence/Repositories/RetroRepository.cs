using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Sprints;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.Persistence.Repositories;

public class RetroRepository : IRetroRepository
{
    private readonly PulseDbContext _db;

    public RetroRepository(PulseDbContext db) => _db = db;

    public async Task<SprintRetrospective?> GetBySprintAsync(Guid sprintId, CancellationToken ct = default) =>
        await _db.SprintRetrospectives.FirstOrDefaultAsync(r => r.SprintId == sprintId, ct);

    public async Task AddAsync(SprintRetrospective retro, CancellationToken ct = default) =>
        await _db.SprintRetrospectives.AddAsync(retro, ct);

    public async Task SaveChangesAsync(CancellationToken ct = default) =>
        await _db.SaveChangesAsync(ct);
}
