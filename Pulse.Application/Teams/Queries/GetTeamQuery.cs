using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Teams.Queries;

public record GetTeamQuery(Guid TeamId) : IRequest<ServiceResult<TeamDto>>;

public class GetTeamHandler : IRequestHandler<GetTeamQuery, ServiceResult<TeamDto>>
{
    private readonly ITeamRepository _teams;

    public GetTeamHandler(ITeamRepository teams) => _teams = teams;

    public async Task<ServiceResult<TeamDto>> Handle(GetTeamQuery query, CancellationToken ct)
    {
        var team = await _teams.GetByIdAsync(query.TeamId, ct);
        if (team is null)
            return ServiceResult<TeamDto>.Fail("NOT_FOUND", $"Team '{query.TeamId}' not found.");

        return ServiceResult<TeamDto>.Ok(TeamDto.From(team));
    }
}
