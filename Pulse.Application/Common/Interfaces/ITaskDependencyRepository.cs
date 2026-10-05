using Pulse.Domain.Tasks;

namespace Pulse.Application.Common.Interfaces;

public interface ITaskDependencyRepository
{
    Task<IReadOnlyList<TaskDependency>> GetBlockingAsync(Guid dependentTaskId, CancellationToken ct = default);
    Task<IReadOnlyList<TaskDependency>> GetDependentsAsync(Guid blockingTaskId, CancellationToken ct = default);
    Task<bool> ExistsAsync(Guid blockingTaskId, Guid dependentTaskId, CancellationToken ct = default);
    Task AddAsync(TaskDependency dependency, CancellationToken ct = default);
    Task RemoveAsync(Guid blockingTaskId, Guid dependentTaskId, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
