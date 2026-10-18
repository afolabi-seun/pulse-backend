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
    string? IpAddress,
    Guid? HeadId = null) : IRequest<ServiceResult<TeamDto>>;

public class CreateTeamHandler : IRequestHandler<CreateTeamCommand, ServiceResult<TeamDto>>
{
    private readonly ITeamRepository _teams;
    private readonly IAuditLogRepository _audit;
    private readonly IEngineerRepository _engineers;

    public CreateTeamHandler(ITeamRepository teams, IAuditLogRepository audit, IEngineerRepository engineers)
    {
        _teams = teams;
        _audit = audit;
        _engineers = engineers;
    }

    public async Task<ServiceResult<TeamDto>> Handle(CreateTeamCommand cmd, CancellationToken ct)
    {
        if (cmd.HeadId is Guid headId && await TeamHead.ValidateAsync(headId, _engineers, ct) is { } problem)
            return ServiceResult<TeamDto>.Fail(problem.Code, problem.Message);

        var team = Team.Create(cmd.Name, cmd.TeamLeadId, cmd.Department ?? cmd.Name);
        team.SetHead(cmd.HeadId);

        await _teams.AddAsync(team, ct);
        await _teams.SaveChangesAsync(ct);

        await _audit.LogAsync("TEAM_CREATED", cmd.ActorId, cmd.IpAddress,
            $"Created team {team.Id} '{team.Name}'", ct);

        return ServiceResult<TeamDto>.Ok(TeamDto.From(team));
    }
}
