using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Teams.Commands;

public record UpdateTeamCommand(
    Guid TeamId,
    string? Name,
    Guid? TeamLeadId,
    bool? ClearTeamLead,
    bool? Deactivate,
    string? Department,
    bool? ClearDepartment,
    Guid ActorId,
    string? IpAddress) : IRequest<ServiceResult<TeamDto>>;

public class UpdateTeamHandler : IRequestHandler<UpdateTeamCommand, ServiceResult<TeamDto>>
{
    private readonly ITeamRepository _teams;
    private readonly IAuditLogRepository _audit;

    public UpdateTeamHandler(ITeamRepository teams, IAuditLogRepository audit)
    {
        _teams = teams;
        _audit = audit;
    }

    public async Task<ServiceResult<TeamDto>> Handle(UpdateTeamCommand cmd, CancellationToken ct)
    {
        var team = await _teams.GetByIdAsync(cmd.TeamId, ct);
        if (team is null)
            return ServiceResult<TeamDto>.Fail("NOT_FOUND", $"Team '{cmd.TeamId}' not found.");

        if (cmd.Name is not null)
            team.Update(cmd.Name);

        if (cmd.ClearTeamLead == true)
            team.SetTeamLead(null);
        else if (cmd.TeamLeadId.HasValue)
            team.SetTeamLead(cmd.TeamLeadId);

        if (cmd.Deactivate == true && team.IsActive)
            team.Deactivate();
        else if (cmd.Deactivate == false && !team.IsActive)
            team.Reactivate();

        if (cmd.ClearDepartment == true)
            team.SetDepartment(null);
        else if (cmd.Department is not null)
            team.SetDepartment(cmd.Department);

        await _teams.SaveChangesAsync(ct);

        await _audit.LogAsync("TEAM_UPDATED", cmd.ActorId, cmd.IpAddress,
            $"Updated team {team.Id} '{team.Name}'", ct);

        return ServiceResult<TeamDto>.Ok(TeamDto.From(team));
    }
}
