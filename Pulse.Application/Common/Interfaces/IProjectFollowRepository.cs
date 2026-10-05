using Pulse.Domain.Projects;

namespace Pulse.Application.Common.Interfaces;

public interface IProjectFollowRepository
{
    Task<ProjectFollow?> GetAsync(Guid followerId, Guid projectId, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> GetFollowedProjectIdsAsync(Guid followerId, CancellationToken ct = default);

    /// <summary>Returns the IDs of engineers who lead teams with members assigned to active tasks on this project.</summary>
    Task<IReadOnlyList<Guid>> GetTeamLeadIdsForProjectAsync(Guid projectId, CancellationToken ct = default);

    Task AddAsync(ProjectFollow follow, CancellationToken ct = default);
    void Remove(ProjectFollow follow);
    Task SaveChangesAsync(CancellationToken ct = default);
}
