using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Engineers.Queries;

/// <summary>Active engineers outside the caller's department — the valid target set for LoanTaskCommand.</summary>
public record GetLoanCandidatesQuery(Guid ActorId, string ActorRole = "") : IRequest<ServiceResult<IReadOnlyList<EngineerDto>>>;

public class GetLoanCandidatesHandler : IRequestHandler<GetLoanCandidatesQuery, ServiceResult<IReadOnlyList<EngineerDto>>>
{
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;

    public GetLoanCandidatesHandler(IEngineerRepository engineers, ITeamRepository teams)
    {
        _engineers = engineers;
        _teams = teams;
    }

    public async Task<ServiceResult<IReadOnlyList<EngineerDto>>> Handle(GetLoanCandidatesQuery query, CancellationToken ct)
    {
        // LoanTaskCommand's own same-department check (for a plain Team Lead — Heads/PMO have no
        // team of their own to compare against there) is keyed off the LED team, via
        // DepartmentScope.GetLedTeamAsync — never the actor's own team MEMBERSHIP, because a team's
        // lead is never auto-added as one of its own members. EngineerIdsAsync resolves department
        // by membership, so using it here for a Team Lead could compute a different department than
        // the one LoanTaskCommand will actually check, showing candidates the write side then
        // rejects (or hiding ones it would actually allow). Match LoanTaskCommand's own resolution
        // instead for that role; Heads/PMO keep the existing membership-based resolution.
        HashSet<Guid>? deptEngineerIds;
        if (!Roles.HeadRoles.Contains(query.ActorRole) && query.ActorRole != Roles.ProjectManager)
        {
            var ledTeam = await DepartmentScope.GetLedTeamAsync(query.ActorId, _teams, ct);
            deptEngineerIds = ledTeam?.Department is string dept
                ? await DepartmentScope.EngineerIdsForDepartmentAsync(dept, _engineers, _teams, ct)
                : null;
        }
        else
        {
            deptEngineerIds = await DepartmentScope.EngineerIdsAsync(query.ActorId, _engineers, _teams, ct);
        }

        var rows = await _engineers.ListWithWorkloadAsync(ct: ct);

        var candidates = deptEngineerIds is null
            ? rows
            : rows.Where(r => !deptEngineerIds.Contains(r.Engineer.Id)).ToList();

        return ServiceResult<IReadOnlyList<EngineerDto>>.Ok(
            candidates.Select(r => EngineerDto.FromWithWorkload(r.Engineer, r.ActiveTasks, r.TotalPoints)).ToList());
    }
}
