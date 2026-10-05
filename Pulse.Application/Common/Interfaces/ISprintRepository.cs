using Pulse.Domain.Sprints;

namespace Pulse.Application.Common.Interfaces;

public interface ISprintRepository
{
    Task<Sprint?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Sprint>> ListByTeamAsync(Guid? teamId, CancellationToken ct = default);
    Task AddAsync(Sprint sprint, CancellationToken ct = default);
    Task RemoveAsync(Sprint sprint, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
