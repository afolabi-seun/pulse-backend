using System.Text;
using Pulse.Application.Auth;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Overwork;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Engineers.Queries;

public record EngineerStatsDto(int ActiveCount, int OverworkedCount, int AvgUtilisation, int TasksInFlight);

public record EngineerListPageDto(IReadOnlyList<EngineerDto> Items, string? NextCursor, bool HasMore, EngineerStatsDto Stats);

/// <summary>Paged, row-filterable variant of <see cref="ListEngineersQuery"/> — used only by the
/// Engineers page's own grid, which needs pagination and a team/status filter on the rendered rows.
/// Everywhere else that needs the full unpaged roster (task/feedback/check-in pages, bulk-reassign
/// pickers) keeps using ListEngineersQuery unchanged.</summary>
public record ListEngineersPageQuery(
    string CallerRole, Guid CallerId,
    int Limit = 24, string? Cursor = null, Guid? TeamId = null, bool? IsActive = null)
    : IRequest<ServiceResult<EngineerListPageDto>>;

public class ListEngineersPageHandler : IRequestHandler<ListEngineersPageQuery, ServiceResult<EngineerListPageDto>>
{
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository     _teams;
    private readonly ITaskRepository     _tasks;
    private readonly EngineerWorkloadAssessor _assessor;

    public ListEngineersPageHandler(IEngineerRepository engineers, ITeamRepository teams, ITaskRepository tasks, EngineerWorkloadAssessor assessor)
    {
        _engineers = engineers;
        _teams     = teams;
        _tasks     = tasks;
        _assessor  = assessor;
    }

    public async Task<ServiceResult<EngineerListPageDto>> Handle(ListEngineersPageQuery query, CancellationToken ct)
    {
        // Same role-scoping as ListEngineersHandler — department heads see their whole department
        // (which can span several teams), a team lead sees only their own team, PMO/PM/Head of
        // Product see everyone. Kept identical rather than shared, since factoring it out would
        // touch the already-correct, already-tested unpaged handler for no behavioral gain here.
        IReadOnlyList<(Domain.Engineers.Engineer Engineer, int ActiveTasks, int TotalPoints)> rows;

        if (query.CallerRole is Roles.HeadOfPmo or Roles.ProjectManager or Roles.HeadOfProduct or Roles.Executive or Roles.HR or Roles.Accountant)
        {
            rows = await _engineers.ListWithWorkloadAsync(ct: ct);
        }
        else if (Roles.HeadRoles.Contains(query.CallerRole))
        {
            var caller = await _engineers.GetByIdAsync(query.CallerId, ct);
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
            // Team lead — their own LED team, not the team they happen to be a member of (see
            // DepartmentScope.GetLedTeamAsync's doc comment: creating/updating a team never adds
            // its lead as one of its own members, so caller.TeamId is the wrong signal here —
            // same bug, same fix as ListEngineersQuery's identical branch).
            var ledTeam = await DepartmentScope.GetLedTeamAsync(query.CallerId, _teams, ct);
            rows = ledTeam is not null
                ? await _engineers.ListWithWorkloadAsync(teamId: ledTeam.Id, ct: ct)
                : await _engineers.ListWithWorkloadAsync(ct: ct);
        }

        // This page is specifically a workload/capacity view (Executive/HR/HeadOfPmo/ProjectManager/
        // Accountant can never carry one — see CheckInExpected, "everyone doing delivery work"), not
        // the general org directory — that's the separate Users admin page, which is unfiltered.
        // Filtered before stats so the tiles below reflect the same delivery-eligible population.
        var deliveryRoles = CapabilityRegistry.All[CapabilityRegistry.CheckInExpected].AllowedRoles;
        rows = rows.Where(r => deliveryRoles.Contains(r.Engineer.Role)).ToList();

        // Stats are computed from the full role-scoped set, deliberately ignoring the team/status
        // row filters below — a head narrowing the grid to one team still sees department-wide
        // stat tiles, matching the page's existing (pre-pagination) behavior.
        var activeRows      = rows.Where(r => r.Engineer.IsActive).ToList();
        // "Overworked" is the overwork signal's verdict — the same one the engineer's own page, the reports and the
        // daily digest use — not "active points over baseline", which flags people for work due months away.
        var workload        = await _assessor.AssessAsync(rows.Select(r => r.Engineer), await _tasks.GetAllActiveAsync(ct), ct);
        var overworkedCount = activeRows.Count(r => workload[r.Engineer.Id].IsOverworked);
        var tasksInFlight   = activeRows.Sum(r => r.ActiveTasks);
        var withBaseline    = activeRows.Where(r => r.Engineer.BaselinePoints > 0).ToList();
        var avgUtilisation  = withBaseline.Count == 0
            ? 0
            // Cycle load ÷ baseline, the same measure as the bars on each card and the overworked verdict — not every
            // active point, which would read as overloaded while few or no one is actually flagged.
            : (int)Math.Round(withBaseline.Average(r => (double)workload[r.Engineer.Id].CyclePoints / r.Engineer.BaselinePoints * 100));
        var stats = new EngineerStatsDto(activeRows.Count, overworkedCount, avgUtilisation, tasksInFlight);

        // Row filters + pagination, applied after stats so they don't affect the tiles above.
        IEnumerable<(Domain.Engineers.Engineer Engineer, int ActiveTasks, int TotalPoints)> filtered = rows;
        if (query.TeamId.HasValue)
            filtered = filtered.Where(r => r.Engineer.TeamId == query.TeamId.Value);
        if (query.IsActive.HasValue)
            filtered = filtered.Where(r => r.Engineer.IsActive == query.IsActive.Value);
        var filteredList = filtered.ToList();

        var limit  = Math.Clamp(query.Limit, 1, 100);
        var offset = query.Cursor is not null
            ? int.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(query.Cursor)))
            : 0;

        var page    = filteredList.Skip(offset).Take(limit + 1).ToList();
        var hasMore = page.Count > limit;
        if (hasMore) page.RemoveAt(page.Count - 1);

        var nextCursor = hasMore
            ? Convert.ToBase64String(Encoding.UTF8.GetBytes((offset + limit).ToString()))
            : null;

        var items = page.Select(r => EngineerDto.FromAssessedWorkload(
            r.Engineer, r.ActiveTasks, r.TotalPoints, workload[r.Engineer.Id].CyclePoints, workload[r.Engineer.Id].IsOverworked)).ToList();

        return ServiceResult<EngineerListPageDto>.Ok(new EngineerListPageDto(items, nextCursor, hasMore, stats));
    }
}
