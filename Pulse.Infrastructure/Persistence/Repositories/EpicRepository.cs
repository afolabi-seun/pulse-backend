using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Epics;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.Persistence.Repositories;

public class EpicRepository : IEpicRepository
{
    private readonly PulseDbContext _db;

    public EpicRepository(PulseDbContext db) => _db = db;

    public async Task<Epic?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.Epics.FirstOrDefaultAsync(e => e.Id == id, ct);

    public async Task<IReadOnlyList<Epic>> ListByProjectAsync(Guid projectId, CancellationToken ct = default) =>
        await _db.Epics.Where(e => e.ProjectId == projectId).OrderBy(e => e.Order).ThenBy(e => e.CreatedAt).ToListAsync(ct);

    public async Task<IReadOnlyList<Epic>> ListBySprintAsync(Guid sprintId, CancellationToken ct = default) =>
        await _db.Epics.Where(e => e.SprintId == sprintId).OrderBy(e => e.Order).ThenBy(e => e.CreatedAt).ToListAsync(ct);

    public async Task<IReadOnlyList<Epic>> ListBacklogByProjectAsync(Guid projectId, CancellationToken ct = default) =>
        await _db.Epics.Where(e => e.ProjectId == projectId && e.SprintId == null).OrderBy(e => e.Order).ThenBy(e => e.CreatedAt).ToListAsync(ct);

    public async Task AddAsync(Epic epic, CancellationToken ct = default) =>
        await _db.Epics.AddAsync(epic, ct);

    public void Remove(Epic epic) => _db.Epics.Remove(epic);

    public async Task SaveChangesAsync(CancellationToken ct = default) =>
        await _db.SaveChangesAsync(ct);
}
