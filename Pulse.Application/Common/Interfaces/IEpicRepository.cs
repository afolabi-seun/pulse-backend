using Pulse.Domain.Epics;

namespace Pulse.Application.Common.Interfaces;

public interface IEpicRepository
{
    Task<Epic?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Epic>> ListByProjectAsync(Guid projectId, CancellationToken ct = default);
    Task<IReadOnlyList<Epic>> ListBySprintAsync(Guid sprintId, CancellationToken ct = default);
    /// <summary>Returns epics that have not been assigned to any sprint (the product backlog).</summary>
    Task<IReadOnlyList<Epic>> ListBacklogByProjectAsync(Guid projectId, CancellationToken ct = default);
    Task AddAsync(Epic epic, CancellationToken ct = default);
    void Remove(Epic epic);
    Task SaveChangesAsync(CancellationToken ct = default);
}
