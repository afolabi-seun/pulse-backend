using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Sprints.Queries;

public record ListSprintsQuery(Guid? TeamId, Guid? ProjectId, Guid ActorId, string ActorRole) : IRequest<ServiceResult<IReadOnlyList<SprintDto>>>;

public class ListSprintsHandler : IRequestHandler<ListSprintsQuery, ServiceResult<IReadOnlyList<SprintDto>>>
{
    private readonly ISprintRepository _sprints;
    private readonly IProjectRepository _projects;
    private readonly IProjectAccessPolicy _access;

    public ListSprintsHandler(ISprintRepository sprints, IProjectRepository projects, IProjectAccessPolicy access)
    {
        _sprints = sprints;
        _projects = projects;
        _access = access;
    }

    public async Task<ServiceResult<IReadOnlyList<SprintDto>>> Handle(ListSprintsQuery query, CancellationToken ct)
    {
        var sprints = await _sprints.ListByTeamAsync(query.TeamId, ct);
        if (query.ProjectId.HasValue)
            sprints = sprints.Where(s => s.ProjectId == query.ProjectId.Value).ToList();

        var projectIds = sprints.Where(s => s.ProjectId.HasValue).Select(s => s.ProjectId!.Value).Distinct().ToList();
        var projectNames = projectIds.Count > 0
            ? await _projects.GetNamesByIdsAsync(projectIds, ct)
            : new Dictionary<Guid, string>();

        // Filter to sprints the caller may see (own team / department / global, or a member-project
        // task in it). Executive/HR/Accountant bypass this shared, write-coupled policy — see the matching
        // comment in GetProjectQuery.
        var visible = new List<SprintDto>();
        foreach (var s in sprints)
            if (Roles.IsOrgReadOnlyViewer(query.ActorRole)
                || await _access.CanAccessSprintAsync(s.Id, s.TeamId, query.ActorId, query.ActorRole, ct))
                visible.Add(SprintDto.From(s, s.ProjectId.HasValue && projectNames.TryGetValue(s.ProjectId.Value, out var pName) ? pName : null));

        return ServiceResult<IReadOnlyList<SprintDto>>.Ok(visible);
    }
}
