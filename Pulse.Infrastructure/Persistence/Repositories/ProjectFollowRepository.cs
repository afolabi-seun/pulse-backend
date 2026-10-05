using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Projects;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.Persistence.Repositories;

public class ProjectFollowRepository : IProjectFollowRepository
{
    private readonly PulseDbContext _db;

    public ProjectFollowRepository(PulseDbContext db) => _db = db;

    public async Task<ProjectFollow?> GetAsync(Guid followerId, Guid projectId, CancellationToken ct = default) =>
        await _db.ProjectFollows
            .FirstOrDefaultAsync(f => f.FollowerId == followerId && f.ProjectId == projectId, ct);

    public async Task<IReadOnlyList<Guid>> GetFollowedProjectIdsAsync(Guid followerId, CancellationToken ct = default) =>
        await _db.ProjectFollows
            .Where(f => f.FollowerId == followerId)
            .Select(f => f.ProjectId)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Guid>> GetTeamLeadIdsForProjectAsync(Guid projectId, CancellationToken ct = default)
    {
        // Find team leads of teams that have engineers assigned to active tasks on this project.
        var teamIds = await _db.Tasks
            .Where(t => t.ProjectId == projectId && t.Status != Domain.Tasks.TaskStatus.Done && t.AssigneeId != null)
            .Join(_db.Engineers, t => t.AssigneeId, e => e.Id, (t, e) => e.TeamId)
            .Where(tid => tid != null)
            .Distinct()
            .ToListAsync(ct);

        return await _db.Teams
            .Where(t => teamIds.Contains(t.Id) && t.TeamLeadId != null)
            .Select(t => t.TeamLeadId!.Value)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task AddAsync(ProjectFollow follow, CancellationToken ct = default) =>
        await _db.ProjectFollows.AddAsync(follow, ct);

    public void Remove(ProjectFollow follow) =>
        _db.ProjectFollows.Remove(follow);

    public async Task SaveChangesAsync(CancellationToken ct = default) =>
        await _db.SaveChangesAsync(ct);
}
