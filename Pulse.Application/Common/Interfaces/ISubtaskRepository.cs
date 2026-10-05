using Pulse.Domain.Tasks;

namespace Pulse.Application.Common.Interfaces;

public interface ISubtaskRepository
{
    Task<Subtask?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Subtask>> GetByTaskIdAsync(Guid taskId, CancellationToken ct = default);
    /// <summary>Batch done/total checklist counts per task — for denormalizing progress onto a
    /// list/board of tasks without an N+1 per-task query. Tasks with no subtasks are simply
    /// absent from the result.</summary>
    Task<IReadOnlyDictionary<Guid, (int Done, int Total)>> CountsByTaskIdsAsync(IEnumerable<Guid> taskIds, CancellationToken ct = default);
    Task AddAsync(Subtask subtask, CancellationToken ct = default);
    Task DeleteAsync(Subtask subtask, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
