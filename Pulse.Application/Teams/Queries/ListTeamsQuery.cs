using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Teams.Queries;

public record ListTeamsQuery(string CallerRole, Guid CallerId) : IRequest<ServiceResult<IReadOnlyList<TeamDto>>>;

/// <summary>
/// Lists every team so cross-department features (e.g. adding a team's members to a project) keep
/// working, but a department head only sees headcount and team-lead identity for their own
/// department — those fields come back null for every other team, mirroring the same
/// own-department-only visibility ListEngineersQuery already applies to the engineer roster itself.
/// </summary>
public class ListTeamsHandler : IRequestHandler<ListTeamsQuery, ServiceResult<IReadOnlyList<TeamDto>>>
{
    private readonly ITeamRepository _teams;
    private readonly IEngineerRepository _engineers;

    public ListTeamsHandler(ITeamRepository teams, IEngineerRepository engineers)
    {
        _teams = teams;
        _engineers = engineers;
    }

    public async Task<ServiceResult<IReadOnlyList<TeamDto>>> Handle(ListTeamsQuery query, CancellationToken ct)
    {
        var teams = await _teams.ListAllAsync(ct);
        var leadIds = teams.Where(t => t.TeamLeadId.HasValue).Select(t => t.TeamLeadId!.Value).Distinct().ToList();
        var leads = leadIds.Count > 0
            ? (await _engineers.GetByIdsAsync(leadIds, ct)).ToDictionary(e => e.Id, e => e.Name)
            : new Dictionary<Guid, string>();
        var counts = await _engineers.CountByTeamAsync(ct);

        string? callerDepartment = null;
        if (query.CallerRole is not (Roles.HeadOfPmo or Roles.ProjectManager or Roles.HeadOfProduct))
        {
            var caller = await _engineers.GetByIdAsync(query.CallerId, ct);
            if (caller?.TeamId is Guid callerTeamId)
                callerDepartment = teams.FirstOrDefault(t => t.Id == callerTeamId)?.Department;
        }
        // No department (global roles, or a head with no team / no department) means unrestricted.
        var restrictToOwnDepartment = callerDepartment is not null;

        var dtos = teams.Select(t =>
        {
            var visible = !restrictToOwnDepartment || t.Department == callerDepartment;
            var leadName = t.TeamLeadId.HasValue && leads.TryGetValue(t.TeamLeadId.Value, out var n) ? n : null;
            var memberCount = counts.TryGetValue(t.Id, out var c) ? c : 0;

            return visible
                ? TeamDto.From(t, leadName, memberCount)
                : TeamDto.From(t, leadName: null, memberCount: null);
        }).ToList();

        return ServiceResult<IReadOnlyList<TeamDto>>.Ok(dtos);
    }
}
