using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.Persistence.Repositories;

public class TaskDependencyRepository : ITaskDependencyRepository
{
    private readonly PulseDbContext _db;

    public TaskDependencyRepository(PulseDbContext db) => _db = db;

    public async Task<IReadOnlyList<TaskDependency>> GetBlockingAsync(Guid dependentTaskId, CancellationToken ct = default) =>
        await _db.TaskDependencies.Where(d => d.DependentTaskId == dependentTaskId).ToListAsync(ct);

    public async Task<IReadOnlyList<TaskDependency>> GetDependentsAsync(Guid blockingTaskId, CancellationToken ct = default) =>
        await _db.TaskDependencies.Where(d => d.BlockingTaskId == blockingTaskId).ToListAsync(ct);

    public async Task<bool> ExistsAsync(Guid blockingTaskId, Guid dependentTaskId, CancellationToken ct = default) =>
        await _db.TaskDependencies.AnyAsync(d => d.BlockingTaskId == blockingTaskId && d.DependentTaskId == dependentTaskId, ct);

    public async Task AddAsync(TaskDependency dependency, CancellationToken ct = default) =>
        await _db.TaskDependencies.AddAsync(dependency, ct);

    public async Task RemoveAsync(Guid blockingTaskId, Guid dependentTaskId, CancellationToken ct = default)
    {
        var dep = await _db.TaskDependencies
            .FirstOrDefaultAsync(d => d.BlockingTaskId == blockingTaskId && d.DependentTaskId == dependentTaskId, ct);
        if (dep is not null) _db.TaskDependencies.Remove(dep);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default) =>
        await _db.SaveChangesAsync(ct);
}
