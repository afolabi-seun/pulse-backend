using Pulse.Application.Auth;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.CheckIns.Queries;

public record CheckInStatusDto(bool CheckedIn, IReadOnlyList<Guid> MissingEngineers);

public record GetCheckInStatusQuery(
    DateOnly Date,
    string CallerRole,
    Guid CallerId) : IRequest<ServiceResult<CheckInStatusDto>>;

public class GetCheckInStatusQueryHandler : IRequestHandler<GetCheckInStatusQuery, ServiceResult<CheckInStatusDto>>
{
    private readonly ICheckInRepository  _checkIns;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository     _teams;

    public GetCheckInStatusQueryHandler(ICheckInRepository checkIns, IEngineerRepository engineers, ITeamRepository teams)
    {
        _checkIns  = checkIns;
        _engineers = engineers;
        _teams     = teams;
    }

    public async Task<ServiceResult<CheckInStatusDto>> Handle(GetCheckInStatusQuery request, CancellationToken ct)
    {
        var allMissing = await _checkIns.GetEngineersWithoutCheckInOnDateAsync(request.Date, ct);
        var allActive  = await _engineers.ListActiveAsync(ct);

        HashSet<Guid> scopedIds;

        if (request.CallerRole is Roles.HeadOfPmo or Roles.ProjectManager or Roles.HeadOfProduct)
        {
            scopedIds = allActive.Select(e => e.Id).ToHashSet();
        }
        else if (Roles.HeadRoles.Contains(request.CallerRole))
        {
            // Scope to engineers on teams in the same department as the caller's team.
            // Falls back to all engineers if the caller has no team or their team has no dept set.
            var caller = allActive.FirstOrDefault(e => e.Id == request.CallerId);
            if (caller?.TeamId is Guid callerTeamId)
            {
                var callerTeam = await _teams.GetByIdAsync(callerTeamId, ct);
                if (callerTeam?.Department is string deptName)
                {
                    var allTeams   = await _teams.ListAllAsync(ct);
                    var deptTeamIds = allTeams
                        .Where(t => t.Department == deptName)
                        .Select(t => t.Id)
                        .ToHashSet();
                    scopedIds = allActive
                        .Where(e => e.TeamId.HasValue && deptTeamIds.Contains(e.TeamId.Value))
                        .Select(e => e.Id)
                        .ToHashSet();
                }
                else
                {
                    scopedIds = allActive.Select(e => e.Id).ToHashSet();
                }
            }
            else
            {
                scopedIds = allActive.Select(e => e.Id).ToHashSet();
            }
        }
        else
        {
            // Team lead — their team only
            var caller = allActive.FirstOrDefault(e => e.Id == request.CallerId);
            scopedIds = caller?.TeamId is not null
                ? allActive.Where(e => e.TeamId == caller.TeamId).Select(e => e.Id).ToHashSet()
                : [request.CallerId];
        }

        // Only ever flag someone actually expected to check in — same CheckInExpected filter as
        // GetStandupSummaryQuery's own "Missing" list (a second, independent re-derivation of the
        // same concept, not shared code, so it needed its own fix). Without this, a caller whose
        // scope includes Executive/HR/PMO/ProjectManager/Accountant would see them as falsely
        // "missing" even though they were never expected to submit a check-in.
        var checkInExpectedRoles = CapabilityRegistry.All[CapabilityRegistry.CheckInExpected].AllowedRoles;
        var roleById = allActive.ToDictionary(e => e.Id, e => e.Role);
        var missing = allMissing
            .Where(id => scopedIds.Contains(id) && roleById.TryGetValue(id, out var role) && checkInExpectedRoles.Contains(role))
            .ToList();
        return ServiceResult<CheckInStatusDto>.Ok(new CheckInStatusDto(missing.Count == 0, missing));
    }
}
