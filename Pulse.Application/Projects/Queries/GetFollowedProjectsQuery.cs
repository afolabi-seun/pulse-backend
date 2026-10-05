using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Projects.Queries;

public record GetFollowedProjectsQuery(Guid FollowerId, string ActorRole) : IRequest<ServiceResult<IReadOnlyList<FollowedProjectDto>>>;

public class GetFollowedProjectsHandler
    : IRequestHandler<GetFollowedProjectsQuery, ServiceResult<IReadOnlyList<FollowedProjectDto>>>
{
    private readonly IProjectFollowRepository _follows;
    private readonly IProjectRepository _projects;
    private readonly ITaskRepository _tasks;
    private readonly ISprintRepository _sprints;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;

    public GetFollowedProjectsHandler(
        IProjectFollowRepository follows,
        IProjectRepository projects,
        ITaskRepository tasks,
        ISprintRepository sprints,
        IEngineerRepository engineers,
        ITeamRepository teams)
    {
        _follows  = follows;
        _projects = projects;
        _tasks    = tasks;
        _sprints  = sprints;
        _engineers = engineers;
        _teams    = teams;
    }

    public async Task<ServiceResult<IReadOnlyList<FollowedProjectDto>>> Handle(
        GetFollowedProjectsQuery query, CancellationToken ct)
    {
        var projectIds = await _follows.GetFollowedProjectIdsAsync(query.FollowerId, ct);
        if (projectIds.Count == 0)
            return ServiceResult<IReadOnlyList<FollowedProjectDto>>.Ok([]);

        // Defense-in-depth: a non-PMO head only sees followed projects their department still staffs,
        // so a stale follow (all dept engineers left) can't keep surfacing another team's project data.
        HashSet<Guid>? deptEngineerIds = query.ActorRole is Roles.HeadOfPmo or Roles.HeadOfProduct
            ? null
            : await DepartmentScope.EngineerIdsAsync(query.FollowerId, _engineers, _teams, ct);

        // Load follow records to get FollowedSince timestamps
        var allProjects = await _projects.ListActiveAsync(ct);
        var projectMap  = allProjects.ToDictionary(p => p.Id);

        // Load all sprints once — filter to Active; keyed by sprint ID
        var activeSprints = (await _sprints.ListByTeamAsync(null, ct))
            .Where(s => s.Status == Domain.Sprints.SprintStatus.Active)
            .ToDictionary(s => s.Id);

        var result = new List<FollowedProjectDto>();

        foreach (var pid in projectIds)
        {
            if (!projectMap.TryGetValue(pid, out var project))
                continue; // project archived since follow was created

            if (deptEngineerIds is not null)
            {
                var memberIds = (await _projects.ListMembersAsync(pid, ct)).Select(m => m.EngineerId);
                if (!memberIds.Any(deptEngineerIds.Contains))
                    continue; // no longer staffed by this head's department
            }

            var counts = await _tasks.GetProjectTaskCountsAsync(pid, ct);

            string?   sprintName    = null;
            DateOnly? sprintEndDate = null;
            if (counts.ActiveSprintId.HasValue && activeSprints.TryGetValue(counts.ActiveSprintId.Value, out var sprint))
            {
                sprintName    = sprint.Name;
                sprintEndDate = sprint.EndDate;
            }

            // Retrieve the follow record for FollowedSince
            var follow = await _follows.GetAsync(query.FollowerId, pid, ct);

            result.Add(new FollowedProjectDto(
                ProjectId:          pid,
                ProjectName:        project.Name,
                IsFollowing:        true,
                ActiveTaskCount:    counts.ActiveCount,
                BlockedTaskCount:   counts.BlockedCount,
                DoneThisSprintCount: counts.DoneThisSprintCount,
                LastBlockerTitle:   counts.LastBlockerTitle,
                ActiveSprintId:     counts.ActiveSprintId,
                ActiveSprintName:   sprintName,
                ActiveSprintEndDate: sprintEndDate,
                FollowedSince:      follow?.CreatedAt ?? project.CreatedAt));
        }

        return ServiceResult<IReadOnlyList<FollowedProjectDto>>.Ok(result);
    }
}
