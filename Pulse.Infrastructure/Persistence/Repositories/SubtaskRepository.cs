using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.Persistence.Repositories;

public class SubtaskRepository : ISubtaskRepository
{
    private readonly PulseDbContext _db;

    public SubtaskRepository(PulseDbContext db) => _db = db;

    public async Task<Subtask?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.Subtasks.FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<IReadOnlyList<Subtask>> GetByTaskIdAsync(Guid taskId, CancellationToken ct = default) =>
        await _db.Subtasks
            .Where(s => s.TaskId == taskId)
            .OrderBy(s => s.CreatedAt)
            .ThenBy(s => s.Id)
            .ToListAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, (int Done, int Total)>> CountsByTaskIdsAsync(IEnumerable<Guid> taskIds, CancellationToken ct = default)
    {
        var ids = taskIds.ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, (int, int)>();

        var rows = await _db.Subtasks
            .Where(s => ids.Contains(s.TaskId))
            .GroupBy(s => s.TaskId)
            .Select(g => new { TaskId = g.Key, Total = g.Count(), Done = g.Count(s => s.IsDone) })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.TaskId, r => (r.Done, r.Total));
    }

    public async Task AddAsync(Subtask subtask, CancellationToken ct = default) =>
        await _db.Subtasks.AddAsync(subtask, ct);

    public Task DeleteAsync(Subtask subtask, CancellationToken ct = default)
    {
        _db.Subtasks.Remove(subtask);
        return Task.CompletedTask;
    }

    public async Task SaveChangesAsync(CancellationToken ct = default) =>
        await _db.SaveChangesAsync(ct);
}
