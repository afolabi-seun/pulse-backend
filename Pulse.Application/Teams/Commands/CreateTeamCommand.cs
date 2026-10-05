using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Teams;
using MediatR;

namespace Pulse.Application.Teams.Commands;

public record CreateTeamCommand(
    string Name,
    Guid? TeamLeadId,
    string? Department,
    Guid ActorId,
    string? IpAddress) : IRequest<ServiceResult<TeamDto>>;

public class CreateTeamHandler : IRequestHandler<CreateTeamCommand, ServiceResult<TeamDto>>
{
    private readonly ITeamRepository _teams;
    private readonly IAuditLogRepository _audit;

    public CreateTeamHandler(ITeamRepository teams, IAuditLogRepository audit)
    {
        _teams = teams;
        _audit = audit;
    }

    public async Task<ServiceResult<TeamDto>> Handle(CreateTeamCommand cmd, CancellationToken ct)
    {
        var team = Team.Create(cmd.Name, cmd.TeamLeadId, cmd.Department ?? cmd.Name);

        await _teams.AddAsync(team, ct);
        await _teams.SaveChangesAsync(ct);

        await _audit.LogAsync("TEAM_CREATED", cmd.ActorId, cmd.IpAddress,
            $"Created team {team.Id} '{team.Name}'", ct);

        return ServiceResult<TeamDto>.Ok(TeamDto.From(team));
    }
}
