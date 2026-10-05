using Pulse.Application.Auth;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Escalations;
using Pulse.Application.Overwork;
using Pulse.Application.Tasks;
using Pulse.Domain.Engineers;
using Pulse.Domain.Sprints;
using Pulse.Domain.Tasks;
using MediatR;

namespace Pulse.Application.Reports.Queries;

public record GetPmoReportQuery(
    string CallerRole,
    Guid CallerId,
    DateOnly? WeekOf = null,
    DateOnly? From = null,
    DateOnly? To = null) : IRequest<ServiceResult<PmoReportDto>>;

public class GetPmoReportHandler : IRequestHandler<GetPmoReportQuery, ServiceResult<PmoReportDto>>
{
    private readonly IEngineerRepository _engineers;
    private readonly ITaskRepository _tasks;
    private readonly IProjectRepository _projects;
    private readonly ITeamRepository _teams;
    private readonly ISprintRepository _sprints;
    private readonly ICheckInRepository _checkIns;
    private readonly ITimeEntryRepository _timeEntries;
    private readonly IProjectFollowRepository _follows;
    private readonly EngineerWorkloadAssessor _assessor;
    private readonly OverworkThresholds _thresholds;

    public GetPmoReportHandler(
        IEngineerRepository engineers,
        ITaskRepository tasks,
        IProjectRepository projects,
        ITeamRepository teams,
        ISprintRepository sprints,
        ICheckInRepository checkIns,
        ITimeEntryRepository timeEntries,
        IProjectFollowRepository follows,
        EngineerWorkloadAssessor assessor,
        OverworkThresholds thresholds)
    {
        _engineers = engineers;
        _tasks = tasks;
        _projects = projects;
        _teams = teams;
        _sprints = sprints;
        _checkIns = checkIns;
        _timeEntries = timeEntries;
        _follows = follows;
        _assessor = assessor;
        _thresholds = thresholds;
    }

    public async Task<ServiceResult<PmoReportDto>> Handle(GetPmoReportQuery query, CancellationToken ct)
    {
        // 1. Date window
        var today = query.WeekOf ?? query.To ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var weekStart = WeekOf.Monday(today);
        var weekEnd = weekStart.AddDays(6);
        var prevWeekStart = weekStart.AddDays(-7);
        var sevenDaysAgo = today.AddDays(-6);
        // Delivered-points window: explicit from/to overrides the week boundary
        var deliveryFrom = query.From ?? weekStart;
        var deliveryTo   = query.To   ?? weekEnd;
        var weekStartDt = deliveryFrom.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var weekEndDt   = deliveryTo.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc);

        // 2. Determine if caller sees all teams or just their own. Executive is read-only but
        // has no team of their own, so — like PMO/Product/PM — always gets the org-wide view.
        // Matches ProjectAccessPolicy.GlobalRoles, which treats ProjectManager/ProductManager as
        // org-wide too — omitting them here left a PM with no team falling into the single-team
        // branch below and always seeing an empty report.
        bool isPmo = query.CallerRole is Roles.HeadOfPmo or Roles.HeadOfProduct or Roles.ProjectManager or Roles.ProductManager or Roles.Executive or Roles.HR or Roles.Accountant;
        Guid? callerTeamId = null;
        if (!isPmo)
        {
            var caller = await _engineers.GetByIdAsync(query.CallerId, ct);
            callerTeamId = caller?.TeamId;
        }

        // 3. Load data sequentially — all repositories share the same scoped DbContext and
        //    EF Core does not allow concurrent operations on the same context instance.
        var allEngineers        = await _engineers.ListActiveAsync(ct);
        var allActiveTasks      = await _tasks.GetAllActiveAsync(ct);
        var blockedTasks        = await _tasks.GetBlockedTasksAsync(ct);
        var allTeams            = await _teams.ListAllAsync(ct);
        var allProjects         = await _projects.ListActiveAsync(ct);
        var escalationLookahead = (int)Math.Ceiling(_thresholds.EscalationT3Days);
        var escalationCandidates = await _tasks.GetEscalationCandidatesAsync(escalationLookahead, ct);

        var checkInCounts = await _checkIns.GetCheckInCountByDateRangeAsync(sevenDaysAgo, today, ct);
        // Hours follow the same deliveryFrom/deliveryTo window as delivered points — previously
        // these silently stayed pinned to weekStart/weekEnd even when the caller passed an explicit
        // from/to, so "Points delivered" would reflect a custom range while every Hours column on
        // the same report kept showing just the single calendar week, with nothing indicating the
        // mismatch.
        // Delivery-only (Task category), not GetHoursByEngineerInRangeAsync's all-category total —
        // this feeds Team Utilization's "Hours" column, a capacity figure that a week of Leave/Admin
        // time shouldn't be able to pad.
        var hoursThisWeek = await _timeEntries.GetDeliveryHoursByEngineerInRangeAsync(deliveryFrom, deliveryTo, null, ct);
        var hoursByProjectThisWeek = await _timeEntries.GetHoursByProjectInRangeAsync(deliveryFrom, deliveryTo, null, ct);
        // Same deliveryFrom/deliveryTo window as Points delivered and Hours above.
        var completedTasksByEngineer = await _tasks.GetCompletedTaskCountByEngineerInRangeAsync(weekStartDt, weekEndDt, ct);
        var subtasksCompletedByEngineer = await _tasks.GetSubtaskCompletionCountByEngineerInRangeAsync(weekStartDt, weekEndDt, ct);

        // 4. Scope teams
        var scopedTeams = isPmo
            ? allTeams
            : allTeams.Where(t => t.Id == callerTeamId).ToList();

        var scopedEngineerIds = isPmo
            ? null as HashSet<Guid>
            : allEngineers.Where(e => e.TeamId == callerTeamId).Select(e => e.Id).ToHashSet();

        // Projects owned by a team outside the caller's own team are invisible to a department
        // head by default — otherwise every head would see the exact same org-wide project list
        // regardless of department, with only the engineer-utilization numbers actually differing.
        // An explicit project membership or a deliberate follow still surfaces it, though — this
        // mirrors ProjectAccessPolicy.CanAccessProjectAsync, so a project a head can actually open
        // is never silently missing from their own report.
        var extraVisibleProjectIds = new HashSet<Guid>();
        if (!isPmo)
        {
            extraVisibleProjectIds.UnionWith(await _projects.GetMemberProjectIdsAsync(query.CallerId, ct));
            extraVisibleProjectIds.UnionWith(await _follows.GetFollowedProjectIdsAsync(query.CallerId, ct));
        }
        var scopedProjects = isPmo
            ? allProjects
            : allProjects.Where(p => p.OwnerTeamId == callerTeamId || extraVisibleProjectIds.Contains(p.Id)).ToList();

        // 5. Prep lookups
        var tasksByAssignee = allActiveTasks
            .Where(t => t.AssigneeId.HasValue && t.Status.CountsAsActiveWorkload())
            .GroupBy(t => t.AssigneeId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Domain.Tasks.PulseTask>)g.ToList());

        // allActiveTasks already includes InQa (it's everything not Done) — a separate, additive
        // view alongside tasksByAssignee, not a broadening of it: see EngineerUtilizationEntry's
        // TasksInQa/PointsInQa comment for why these stay apart from ActiveTasks/TotalPoints.
        var inQaTasksByAssignee = allActiveTasks
            .Where(t => t.AssigneeId.HasValue && t.Status == Domain.Tasks.TaskStatus.InQa)
            .GroupBy(t => t.AssigneeId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Domain.Tasks.PulseTask>)g.ToList());

        var blockersByAssignee = blockedTasks
            .Where(t => t.AssigneeId.HasValue)
            .GroupBy(t => t.AssigneeId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        var workloadByEngineer = await _assessor.AssessAsync(allEngineers, allActiveTasks, ct);

        var engineerNameById = allEngineers.ToDictionary(e => e.Id, e => e.Name);
        var projectNameById = allProjects.ToDictionary(p => p.Id, p => p.Name);
        var projectCodeById = allProjects.ToDictionary(p => p.Id, p => p.Code);

        // 6. Escalations per project
        var now = DateTime.UtcNow;
        var escalationsByProject = new Dictionary<Guid, int>();
        foreach (var t in escalationCandidates)
        {
            if (t.DueDate is null) continue;
            var level = EscalationLevelCalculator.Determine(t.DueDate.Value, t.ActivatedAt, _thresholds, now);

            if (level is not null)
                escalationsByProject[t.ProjectId] = escalationsByProject.GetValueOrDefault(t.ProjectId) + 1;
        }

        // Everything the Health column needs to explain itself: overdue vs due-soon tasks and blockers (aged
        // in working days from when they were blocked), per project. See ProjectHealthCalculator.
        var blockedSince = await _tasks.GetBlockedSinceAsync(blockedTasks.Select(t => t.Id).ToList(), ct);
        var (overdueByProject, dueSoonByProject) = ProjectHealthInputs.FromEscalations(
            escalationCandidates, _ => true, projectCodeById, engineerNameById, _thresholds, now);
        var blockedByProject = ProjectHealthInputs.FromBlocked(
            blockedTasks, _ => true, blockedSince, projectCodeById, engineerNameById, now);

        // 7. Team Utilization — Executive/HeadOfPmo/ProjectManager/Accountant can never carry a
        // delivery workload (see CheckInExpected, "everyone doing delivery work"), so a team that
        // happens to include one would otherwise show a permanent 0-points, non-overworked entry
        // diluting the team's average load. Same filter used for Check-in Compliance below.
        var deliveryRoles = CapabilityRegistry.All[CapabilityRegistry.CheckInExpected].AllowedRoles;
        var teamUtilization = new List<TeamUtilizationDto>();
        foreach (var team in scopedTeams)
        {
            var teamEngineers = allEngineers.Where(e => e.TeamId == team.Id && deliveryRoles.Contains(e.Role)).ToList();
            var entries = teamEngineers.Select(engineer => UtilizationEntryBuilder.Build(
                engineer, tasksByAssignee, inQaTasksByAssignee,
                checkInCounts, blockersByAssignee, hoursThisWeek, completedTasksByEngineer,
                subtasksCompletedByEngineer, workloadByEngineer)).ToList();

            int overworkedCount = entries.Count(e => e.IsOverworked);
            // Null (not 0) when the team has nobody delivery-eligible to average over — e.g. every
            // remaining member is a role TimeEntrySubmitter/CheckInExpected already excludes (see
            // the roster filter above), or the team is genuinely empty. A literal 0% here used to
            // read as "this team is completely idle" when the real story is "there's no one on this
            // team a load figure could even apply to." BaselinePoints itself can never be <= 0 for a
            // real Engineer (Engineer.Create/UpdateBaseline both reject it), so filtering on it is
            // just future-proofing against any anomalous data — entries.Count == 0 is the only
            // practically reachable trigger today.
            int? avgLoadPct = entries.Count > 0
                ? (int)Math.Round(entries.Average(e => (double)e.TotalPoints / e.BaselinePoints * 100))
                : null;

            teamUtilization.Add(new TeamUtilizationDto(team.Id, team.Name, entries, overworkedCount, avgLoadPct));
        }

        // 8. Project Health — load all sprints once, then look up by ID
        var allSprints = await _sprints.ListByTeamAsync(null, ct);
        var sprintById = allSprints.ToDictionary(s => s.Id);

        var scopedProjectIds = scopedProjects.Select(p => p.Id).ToList();
        var taskCountsByProject = await _tasks.GetProjectTaskCountsBatchAsync(scopedProjectIds, ct);

        var projectHealth = new List<ProjectHealthDto>();
        foreach (var project in scopedProjects)
        {
            var counts = taskCountsByProject[project.Id];
            var escalCount = escalationsByProject.GetValueOrDefault(project.Id, 0);

            string? sprintName = null;
            if (counts.ActiveSprintId.HasValue && sprintById.TryGetValue(counts.ActiveSprintId.Value, out var activeSprint))
                sprintName = activeSprint.Name;

            var assessment = ProjectHealthCalculator.Assess(
                overdueByProject.GetValueOrDefault(project.Id) ?? [],
                dueSoonByProject.GetValueOrDefault(project.Id) ?? [],
                blockedByProject.GetValueOrDefault(project.Id) ?? [],
                _thresholds);

            var completionPct = counts.TotalCount > 0
                ? (int)Math.Round((double)counts.DoneCount / counts.TotalCount * 100)
                : 0;

            projectHealth.Add(new ProjectHealthDto(
                project.Id, project.Name,
                counts.ActiveCount, counts.BlockedCount, counts.DoneThisSprintCount,
                counts.TotalCount, completionPct,
                escalCount, sprintName, assessment.Health,
                hoursByProjectThisWeek.GetValueOrDefault(project.Id, 0m),
                counts.HighPriorityOpenCount,
                assessment.Reasons, assessment.NextSteps,
                (overdueByProject.GetValueOrDefault(project.Id)?.Count) ?? 0,
                (dueSoonByProject.GetValueOrDefault(project.Id)?.Count) ?? 0));
        }

        // 9. Sprint Velocity (last 4 completed sprints per team) — one batched task query across every
        //    team's sprints instead of one round trip per sprint.
        var teamSprintsMap = scopedTeams.ToDictionary(
            team => team.Id,
            team => allSprints
                .Where(s => s.TeamId == team.Id && s.Status == SprintStatus.Completed)
                .OrderByDescending(s => s.EndDate)
                .Take(4)
                .ToList());

        var velocitySprintIds = teamSprintsMap.Values.SelectMany(s => s).Select(s => s.Id).Distinct().ToList();
        var tasksBySprintId = await _tasks.GetBySprintIdsAsync(velocitySprintIds, ct);

        var sprintVelocity = new List<TeamSprintVelocityDto>();
        foreach (var team in scopedTeams)
        {
            var velocityEntries = teamSprintsMap[team.Id]
                .Select(sprint =>
                {
                    var sprintTasks = tasksBySprintId.TryGetValue(sprint.Id, out var t) ? t : Array.Empty<Domain.Tasks.PulseTask>();
                    // QA sub-tasks carry their own copy of the parent's points — excluded so a
                    // task that went through QA isn't counted as two separate pieces of scope.
                    var delivered = sprintTasks.ExcludingQaSubtasks()
                        .Where(t => t.Status == Domain.Tasks.TaskStatus.Done).Sum(t => t.Points);
                    return new SprintVelocityEntry(sprint.Name, sprint.CapacityPoints, delivered);
                })
                .Reverse() // chronological order
                .ToList();
            sprintVelocity.Add(new TeamSprintVelocityDto(team.Id, team.Name, velocityEntries));
        }

        // 10. Check-in Compliance (last 4 weeks) — each week's check-in counts only depend on the date
        //     range, not the team, so fetch each week once and reuse it across every team.
        var complianceWeekStarts = Enumerable.Range(0, 4).Select(w => weekStart.AddDays(-7 * (3 - w))).ToList();
        var checkInsByWeek = new List<IReadOnlyDictionary<Guid, int>>();
        foreach (var wStart in complianceWeekStarts)
            checkInsByWeek.Add(await _checkIns.GetCheckInCountByDateRangeAsync(wStart, wStart.AddDays(6), ct));

        var compliance = new List<TeamComplianceDto>();
        var allTeamEngineers = allEngineers.Where(e => e.TeamId.HasValue && deliveryRoles.Contains(e.Role)).ToList();

        foreach (var team in scopedTeams)
        {
            var teamEngIds = allTeamEngineers.Where(e => e.TeamId == team.Id).Select(e => e.Id).ToHashSet();
            var teamEngCount = teamEngIds.Count;
            if (teamEngCount == 0) continue;

            var weekEntries = new List<WeekComplianceDto>();
            for (int i = 0; i < complianceWeekStarts.Count; i++)
            {
                var wStart = complianceWeekStarts[i];
                var weekCheckIns = checkInsByWeek[i];
                var checkedIn = weekCheckIns.Keys.Count(id => teamEngIds.Contains(id) && weekCheckIns[id] > 0);
                var pct = (int)Math.Round((double)checkedIn / teamEngCount * 100);
                weekEntries.Add(new WeekComplianceDto(wStart.ToString("yyyy-MM-dd"), teamEngCount, checkedIn, pct));
            }
            compliance.Add(new TeamComplianceDto(team.Id, team.Name, weekEntries));
        }

        // 11. Blocker Aging
        var blockerAging = blockedTasks
            .Where(t => isPmo || (t.AssigneeId.HasValue && scopedEngineerIds != null && scopedEngineerIds.Contains(t.AssigneeId.Value)))
            .Select(t =>
            {
                engineerNameById.TryGetValue(t.AssigneeId ?? Guid.Empty, out var assigneeName);
                projectNameById.TryGetValue(t.ProjectId, out var projectName);
                projectCodeById.TryGetValue(t.ProjectId, out var projectCode);
                var days = ProjectHealthInputs.BlockedDays(t, blockedSince, now);
                var taskKey = projectCode is not null ? $"{projectCode}-{t.TaskNumber}" : null;
                return new BlockerAgingDto(t.Id, t.Title, assigneeName, projectName, t.BlockerReason, days, taskKey);
            })
            .OrderByDescending(b => b.DaysBlocked)
            .ToList();

        // 12. Delivered points
        var rangeLength = (deliveryTo.DayNumber - deliveryFrom.DayNumber) + 1;
        var prevStartDt = deliveryFrom.AddDays(-rangeLength).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var prevEndDt   = weekStartDt.AddTicks(-1);
        // PMO keeps the true org-wide total (including archived projects, which scopedProjects
        // excludes); department heads get the same department scoping as project health above.
        var totalDelivered = isPmo
            ? await _tasks.GetDeliveredPointsInRangeAsync(weekStartDt, weekEndDt, ct)
            : await _tasks.GetDeliveredPointsInRangeAsync(weekStartDt, weekEndDt, scopedProjectIds, ct);
        var prevWeekPts = isPmo
            ? await _tasks.GetDeliveredPointsInRangeAsync(prevStartDt, prevEndDt, ct)
            : await _tasks.GetDeliveredPointsInRangeAsync(prevStartDt, prevEndDt, scopedProjectIds, ct);
        var avgCycleTimeDays = isPmo
            ? await _tasks.GetAvgCycleTimeDaysInRangeAsync(weekStartDt, weekEndDt, ct)
            : await _tasks.GetAvgCycleTimeDaysInRangeAsync(weekStartDt, weekEndDt, scopedProjectIds, ct);
        var avgPrApprovalHours = isPmo
            ? await _tasks.GetAvgPrApprovalHoursInRangeAsync(weekStartDt, weekEndDt, ct)
            : await _tasks.GetAvgPrApprovalHoursInRangeAsync(weekStartDt, weekEndDt, scopedProjectIds, ct);

        return ServiceResult<PmoReportDto>.Ok(new PmoReportDto(
            weekStart.ToString("yyyy-MM-dd"),
            deliveryFrom.ToString("yyyy-MM-dd"),
            deliveryTo.ToString("yyyy-MM-dd"),
            totalDelivered,
            prevWeekPts,
            teamUtilization,
            projectHealth,
            sprintVelocity,
            compliance,
            blockerAging,
            avgCycleTimeDays,
            avgPrApprovalHours));
    }
}
