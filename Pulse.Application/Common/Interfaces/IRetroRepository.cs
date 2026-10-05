using Pulse.Domain.Sprints;

namespace Pulse.Application.Common.Interfaces;

public interface IRetroRepository
{
    Task<SprintRetrospective?> GetBySprintAsync(Guid sprintId, CancellationToken ct = default);
    Task AddAsync(SprintRetrospective retro, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
