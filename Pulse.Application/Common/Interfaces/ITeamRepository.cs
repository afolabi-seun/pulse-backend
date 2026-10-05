using Pulse.Domain.Teams;

namespace Pulse.Application.Common.Interfaces;

public interface ITeamRepository
{
    Task<Team?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Team>> ListAllAsync(CancellationToken ct = default);
    Task AddAsync(Team team, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
