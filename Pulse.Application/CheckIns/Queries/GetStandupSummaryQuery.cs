using Pulse.Application.Auth;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.CheckIns.Queries;

public record StandupEntryDto(
    Guid EngineerId,
    string EngineerName,
    string Role,
    string? TeamName,
    string Completed,
    string PlannedNext,
    string? Blockers,
    Guid? ProjectId = null,
    string? ProjectName = null);

public record MissingEngineerDto(Guid Id, string Name);

public record StandupSummaryDto(
    DateOnly Date,
    Guid? TeamId,
    PagedResultDto<StandupEntryDto> Entries,
    IReadOnlyList<MissingEngineerDto> MissingEngineers);

public record GetStandupSummaryQuery(
    Guid? TeamId,
    DateOnly? Date,
    string CallerRole,
    Guid CallerId,
    int Limit = 25,
    string? Cursor = null,
    // Bypasses Limit/Cursor entirely and returns every scoped entry in one page — for the CSV
    // export, which needs the whole day's digest rather than one screen's worth of rows.
    bool All = false) : IRequest<ServiceResult<StandupSummaryDto>>;

public class GetStandupSummaryHandler : IRequestHandler<GetStandupSummaryQuery, ServiceResult<StandupSummaryDto>>
{
    private readonly ICheckInRepository _checkIns;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;
    private readonly IProjectRepository _projects;

    public GetStandupSummaryHandler(
        ICheckInRepository checkIns,
        IEngineerRepository engineers,
        ITeamRepository teams,
        IProjectRepository projects)
    {
        _checkIns = checkIns;
        _engineers = engineers;
        _teams = teams;
        _projects = projects;
    }

    public async Task<ServiceResult<StandupSummaryDto>> Handle(GetStandupSummaryQuery query, CancellationToken ct)
    {
        var date = query.Date ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var allEngineers = await _engineers.ListActiveAsync(ct);

        // Derive the base set of visible engineers from the caller's role
        List<Domain.Engineers.Engineer> baseEngineers;

        if (query.CallerRole is Roles.HeadOfPmo or Roles.ProjectManager or Roles.HeadOfProduct or Roles.Executive or Roles.HR or Roles.Accountant)
        {
            baseEngineers = allEngineers.ToList();
        }
        else if (Roles.HeadRoles.Contains(query.CallerRole))
        {
            var caller = allEngineers.FirstOrDefault(e => e.Id == query.CallerId);
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
                    baseEngineers = allEngineers
                        .Where(e => e.TeamId.HasValue && deptTeamIds.Contains(e.TeamId.Value))
                        .ToList();
                }
                else
                {
                    // Head has a team but it has no department set — show only their team
                    baseEngineers = allEngineers
                        .Where(e => e.TeamId == callerTeamId)
                        .ToList();
                }
            }
            else
            {
                // Head has no team assigned — show only themselves
                baseEngineers = caller is not null ? [caller] : [];
            }
        }
        else
        {
            // Team lead — locked to their own team
            var caller = allEngineers.FirstOrDefault(e => e.Id == query.CallerId);
            baseEngineers = caller?.TeamId is not null
                ? allEngineers.Where(e => e.TeamId == caller.TeamId).ToList()
                : allEngineers.Where(e => e.Id == query.CallerId).ToList();
        }

        // Optionally narrow further by teamId (valid only within the base set)
        List<Domain.Engineers.Engineer> scopedEngineers;
        if (query.TeamId.HasValue)
        {
            var team = await _teams.GetByIdAsync(query.TeamId.Value, ct);
            if (team is null)
                return ServiceResult<StandupSummaryDto>.Fail("NOT_FOUND", "Team not found.");
            scopedEngineers = baseEngineers.Where(e => e.TeamId == query.TeamId.Value).ToList();
        }
        else
        {
            scopedEngineers = baseEngineers;
        }

        var engineerIds  = scopedEngineers.Select(e => e.Id).ToList();
        var checkIns     = await _checkIns.GetByDateAsync(date, engineerIds, ct);
        var checkInByEng = checkIns.ToLookup(c => c.EngineerId);

        // Resolve project names for any project-tagged check-ins
        var projectIds = checkIns
            .Where(c => c.ProjectId.HasValue)
            .Select(c => c.ProjectId!.Value)
            .Distinct()
            .ToList();

        Dictionary<Guid, string> projectNames = [];
        if (projectIds.Count > 0)
        {
            var allProjects = await _projects.ListActiveAsync(ct);
            projectNames = allProjects
                .Where(p => projectIds.Contains(p.Id))
                .ToDictionary(p => p.Id, p => p.Name);
        }

        // One entry per check-in (engineers on multiple projects produce multiple entries).
        // Ordered explicitly (name, then id/project as tiebreakers) so the cursor below is stable —
        // scopedEngineers' own order isn't a sort, just whatever ListActiveAsync happened to return.
        var entries = scopedEngineers
            .Where(e => checkInByEng[e.Id].Any())
            .SelectMany(e => checkInByEng[e.Id].Select(c => new StandupEntryDto(
                e.Id, e.Name, e.Role, e.Team,
                c.Completed, c.PlannedNext, c.Blockers,
                c.ProjectId,
                c.ProjectId.HasValue ? projectNames.GetValueOrDefault(c.ProjectId.Value) : null)))
            .OrderBy(e => e.EngineerName).ThenBy(e => e.EngineerId).ThenBy(e => e.ProjectId)
            .ToList();

        // "Missing" should only ever flag someone who was actually expected to check in — uses the
        // CheckInExpected role set (the "everyone doing delivery work" grouping) rather than
        // TimeEntrySubmitter, which HR now also belongs to for time-tracking purposes without
        // being expected to do daily standups — see both doc comments in CapabilityRegistry.
        // HR/Executive/PMO/ProjectManager never show up here even when they're in scope (e.g. an
        // HR caller sees every engineer, including other HR/Exec people, who were never expected
        // to submit a standup). Submitting one is still allowed and still shown under Entries —
        // this only narrows who can be flagged as missing.
        var checkInExpectedRoles = CapabilityRegistry.All[CapabilityRegistry.CheckInExpected].AllowedRoles;
        var missingEngineers = scopedEngineers
            .Where(e => checkInExpectedRoles.Contains(e.Role) && !checkInByEng[e.Id].Any())
            .Select(e => new MissingEngineerDto(e.Id, e.Name))
            .ToList();

        if (query.All)
        {
            return ServiceResult<StandupSummaryDto>.Ok(
                new StandupSummaryDto(date, query.TeamId, new PagedResultDto<StandupEntryDto>(entries, null, false), missingEngineers));
        }

        // Entries are already fully computed above (bounded by one day's scoped headcount, cheap)
        // — this just bounds the response payload, same Limit/Cursor convention as every other
        // paginated list endpoint, rather than a real DB-level page.
        var limit  = Math.Clamp(query.Limit, 1, 100);
        var offset = query.Cursor is not null
            ? int.Parse(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(query.Cursor)))
            : 0;
        var slice   = entries.Skip(offset).Take(limit + 1).ToList();
        var hasMore = slice.Count > limit;
        var page    = hasMore ? slice.Take(limit).ToList() : slice;
        var nextCursor = hasMore
            ? Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes((offset + limit).ToString()))
            : null;

        return ServiceResult<StandupSummaryDto>.Ok(
            new StandupSummaryDto(date, query.TeamId, new PagedResultDto<StandupEntryDto>(page, nextCursor, hasMore), missingEngineers));
    }
}
