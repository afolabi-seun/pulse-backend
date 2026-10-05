using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Engineers.Queries;

public record ListEngineersQuery(string CallerRole, Guid CallerId) : IRequest<ServiceResult<IReadOnlyList<EngineerDto>>>;

public class ListEngineersHandler : IRequestHandler<ListEngineersQuery, ServiceResult<IReadOnlyList<EngineerDto>>>
{
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository     _teams;

    public ListEngineersHandler(IEngineerRepository engineers, ITeamRepository teams)
    {
        _engineers = engineers;
        _teams     = teams;
    }

    public async Task<ServiceResult<IReadOnlyList<EngineerDto>>> Handle(ListEngineersQuery request, CancellationToken ct)
    {
        IReadOnlyList<(Domain.Engineers.Engineer Engineer, int ActiveTasks, int TotalPoints)> rows;

        if (request.CallerRole is Roles.HeadOfPmo or Roles.ProjectManager or Roles.HeadOfProduct or Roles.Executive or Roles.HR or Roles.Accountant)
        {
            rows = await _engineers.ListWithWorkloadAsync(ct: ct);
        }
        else if (Roles.HeadRoles.Contains(request.CallerRole))
        {
            var caller = await _engineers.GetByIdAsync(request.CallerId, ct);
            if (caller?.TeamId is Guid callerTeamId)
            {
                var callerTeam = await _teams.GetByIdAsync(callerTeamId, ct);
                if (callerTeam?.Department is string deptName)
                {
                    var allTeams    = await _teams.ListAllAsync(ct);
                    var deptTeamIds = allTeams
                        .Where(t => t.Department == deptName)
                        .Select(t => t.Id)
                        .ToHashSet();
                    var all = await _engineers.ListWithWorkloadAsync(ct: ct);
                    rows = all
                        .Where(r => r.Engineer.TeamId.HasValue && deptTeamIds.Contains(r.Engineer.TeamId.Value))
                        .ToList();
                }
                else
                {
                    rows = await _engineers.ListWithWorkloadAsync(teamId: callerTeamId, ct: ct);
                }
            }
            else
            {
                rows = await _engineers.ListWithWorkloadAsync(ct: ct);
            }
        }
        else
        {
            // Team lead — their own LED team, not the team they happen to be a member of. Those
            // two can differ: a team's lead is never automatically added as one of its own
            // members (see DepartmentScope.GetLedTeamAsync's own doc comment), so scoping by
            // caller.TeamId here silently showed the wrong roster — or none — whenever a lead
            // isn't personally on the team they lead, hiding their real teammates from every
            // assign/reassign picker that depends on this endpoint.
            var ledTeam = await DepartmentScope.GetLedTeamAsync(request.CallerId, _teams, ct);
            rows = ledTeam is not null
                ? await _engineers.ListWithWorkloadAsync(teamId: ledTeam.Id, ct: ct)
                : await _engineers.ListWithWorkloadAsync(ct: ct);
        }

        return ServiceResult<IReadOnlyList<EngineerDto>>.Ok(
            rows.Select(r => EngineerDto.FromWithWorkload(r.Engineer, r.ActiveTasks, r.TotalPoints)).ToList());
    }
}
