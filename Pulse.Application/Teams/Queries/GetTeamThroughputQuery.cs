using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Projects.Queries;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Teams.Queries;

public record GetTeamThroughputQuery(Guid TeamId, Guid ActorId, string ActorRole) : IRequest<ServiceResult<IReadOnlyList<ThroughputWeekDto>>>;

/// <summary>Returns delivered story points per week over the 6-week rolling window ending today,
/// summed across every engineer on the given team — the team-level analogue of
/// GetEngineerThroughputQuery/GetProjectThroughputQuery.</summary>
public class GetTeamThroughputHandler : IRequestHandler<GetTeamThroughputQuery, ServiceResult<IReadOnlyList<ThroughputWeekDto>>>
{
    private readonly ITeamRepository _teams;
    private readonly ITaskRepository _tasks;
    private readonly IProjectAccessPolicy _access;

    public GetTeamThroughputHandler(ITeamRepository teams, ITaskRepository tasks, IProjectAccessPolicy access)
    {
        _teams = teams;
        _tasks = tasks;
        _access = access;
    }

    public async Task<ServiceResult<IReadOnlyList<ThroughputWeekDto>>> Handle(GetTeamThroughputQuery query, CancellationToken ct)
    {
        var team = await _teams.GetByIdAsync(query.TeamId, ct);
        if (team is null)
            return ServiceResult<IReadOnlyList<ThroughputWeekDto>>.Fail("NOT_FOUND", "Team not found.");

        // Executive is read-only org-wide via its own explicit check rather than joining
        // ProjectAccessPolicy.GlobalRoles, since that set also gates writes elsewhere and
        // Executive must never inherit those (mirrors ListProjectsQuery).
        if (query.ActorRole is not (Roles.Executive or Roles.HR) && !await _access.CanAccessTeamAsync(query.TeamId, query.ActorId, query.ActorRole, ct))
            return ServiceResult<IReadOnlyList<ThroughputWeekDto>>.Fail("FORBIDDEN", "You do not have access to this team.");

        var points = await _tasks.GetWeeklyThroughputByTeamAsync(query.TeamId, ct);
        var result = points.Select(p => new ThroughputWeekDto(p.WeekOf, p.PointsDelivered)).ToList();

        return ServiceResult<IReadOnlyList<ThroughputWeekDto>>.Ok(result);
    }
}
