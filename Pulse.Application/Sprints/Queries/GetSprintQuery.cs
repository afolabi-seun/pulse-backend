using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Sprints.Queries;

public record GetSprintQuery(Guid SprintId, Guid ActorId, string ActorRole) : IRequest<ServiceResult<SprintDto>>;

public class GetSprintHandler : IRequestHandler<GetSprintQuery, ServiceResult<SprintDto>>
{
    private readonly ISprintRepository _sprints;
    private readonly IProjectRepository _projects;
    private readonly ITeamRepository _teams;
    private readonly IProjectAccessPolicy _access;

    public GetSprintHandler(ISprintRepository sprints, IProjectRepository projects, ITeamRepository teams, IProjectAccessPolicy access)
    {
        _sprints = sprints;
        _projects = projects;
        _teams = teams;
        _access = access;
    }

    public async Task<ServiceResult<SprintDto>> Handle(GetSprintQuery query, CancellationToken ct)
    {
        var sprint = await _sprints.GetByIdAsync(query.SprintId, ct);
        if (sprint is null)
            return ServiceResult<SprintDto>.Fail("NOT_FOUND", $"Sprint '{query.SprintId}' not found.");

        // Executive/HR/Accountant bypass the shared, write-coupled ProjectAccessPolicy here — see the
        // matching comment in GetProjectQuery.
        var allowed = Roles.IsOrgReadOnlyViewer(query.ActorRole)
            || await _access.CanAccessSprintAsync(sprint.Id, sprint.TeamId, query.ActorId, query.ActorRole, ct);
        if (!allowed)
            return ServiceResult<SprintDto>.Fail("FORBIDDEN", "You do not have access to this sprint.");

        string? projectName = null;
        if (sprint.ProjectId.HasValue)
        {
            var names = await _projects.GetNamesByIdsAsync([sprint.ProjectId.Value], ct);
            names.TryGetValue(sprint.ProjectId.Value, out projectName);
        }

        // The caller's access was already verified against this sprint's team above
        // (CanAccessSprintAsync) — the team's plain name isn't sensitive beyond that, so it's
        // looked up directly rather than gated behind GetTeamQuery's narrower PmOrAbove
        // capability, which a team lead viewing their own sprint would otherwise fail.
        var team = await _teams.GetByIdAsync(sprint.TeamId, ct);

        return ServiceResult<SprintDto>.Ok(SprintDto.From(sprint, projectName, team?.Name));
    }
}
