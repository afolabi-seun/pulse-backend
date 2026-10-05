using Pulse.Domain.Tasks;

namespace Pulse.Application.Common.Interfaces;

public interface ITaskCommentRepository
{
    Task<IReadOnlyList<TaskComment>> GetByTaskAsync(Guid taskId, CancellationToken ct = default);
    Task<TaskComment?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task AddAsync(TaskComment comment, CancellationToken ct = default);
    Task DeleteAsync(TaskComment comment, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
