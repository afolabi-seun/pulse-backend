using Pulse.Application.Auth;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Escalations;
using Pulse.Application.Overwork;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using MediatR;

namespace Pulse.Application.Reports.Queries;

public record GetLeadershipReportQuery(Guid ActorId, string ActorRole, DateOnly? WeekOf = null) : IRequest<ServiceResult<LeadershipReportDto>>;

public class GetLeadershipReportHandler : IRequestHandler<GetLeadershipReportQuery, ServiceResult<LeadershipReportDto>>
{
    private readonly IEngineerRepository _engineers;
    private readonly ITaskRepository _tasks;
    private readonly ICheckInRepository _checkIns;
    private readonly EngineerWorkloadAssessor _assessor;
    private readonly OverworkThresholds _thresholds;
    private readonly ITeamRepository _teams;
    private readonly IProjectRepository _projects;
    private readonly ITimeEntryRepository _timeEntries;

    public GetLeadershipReportHandler(
        IEngineerRepository engineers,
        ITaskRepository tasks,
        ICheckInRepository checkIns,
        EngineerWorkloadAssessor assessor,
        OverworkThresholds thresholds,
        ITeamRepository teams,
        IProjectRepository projects,
        ITimeEntryRepository timeEntries)
    {
        _engineers = engineers;
        _tasks = tasks;
        _checkIns = checkIns;
        _assessor = assessor;
        _thresholds = thresholds;
        _teams = teams;
        _projects = projects;
        _timeEntries = timeEntries;
    }

    public async Task<ServiceResult<LeadershipReportDto>> Handle(GetLeadershipReportQuery query, CancellationToken ct)
    {
        var today = query.WeekOf ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var weekStart = WeekOf.Monday(today);
        var weekEnd = weekStart.AddDays(6);
        var prevWeekStart = weekStart.AddDays(-7);

        var weekStartDt = weekStart.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var weekEndDt   = weekEnd.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc);
        var prevStartDt = prevWeekStart.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var prevEndDt   = weekStartDt.AddTicks(-1);

        var sevenDaysAgo = today.AddDays(-6);

        var allEngineers = await _engineers.ListActiveAsync(ct);
        var allActiveTasks = await _tasks.GetAllActiveAsync(ct);
        var checkInCounts = await _checkIns.GetCheckInCountByDateRangeAsync(sevenDaysAgo, today, ct);
        var escalationLookahead = (int)Math.Ceiling(_thresholds.EscalationT3Days);
        var escalationCandidates = (await _tasks.GetEscalationCandidatesAsync(escalationLookahead, ct)).AsEnumerable();
        var blockedTasks = (await _tasks.GetBlockedTasksAsync(ct)).AsEnumerable();

        // Non-PMO/PM heads see only their own department's engineers, escalations, and blockers.
        // Executive and HR are explicit org-wide roles here (not merely relying on them typically
        // having no team, which DepartmentScope.EngineerIdsAsync would also resolve to null) —
        // matching how every other org-wide reporting query in this codebase treats them.
        // (The org-wide delivered-points headline figures are aggregate velocity, not per-person, so they stay org-wide.)
        var deptEngineerIds = query.ActorRole is Roles.HeadOfPmo or Roles.ProjectManager or Roles.HeadOfProduct
            or Roles.Executive or Roles.HR or Roles.Accountant
            ? null
            : await DepartmentScope.EngineerIdsAsync(query.ActorId, _engineers, _teams, ct);
        if (deptEngineerIds is not null)
        {
            allEngineers = allEngineers.Where(e => deptEngineerIds.Contains(e.Id)).ToList();
            escalationCandidates = escalationCandidates.Where(t => t.AssigneeId.HasValue && deptEngineerIds.Contains(t.AssigneeId.Value));
            blockedTasks = blockedTasks.Where(t => t.AssigneeId.HasValue && deptEngineerIds.Contains(t.AssigneeId.Value));
        }

        var totalDeliveredPoints  = await _tasks.GetDeliveredPointsInRangeAsync(weekStartDt, weekEndDt, ct);
        var previousWeekPoints    = await _tasks.GetDeliveredPointsInRangeAsync(prevStartDt, prevEndDt, ct);
        var subtasksCompletedByEngineer = await _tasks.GetSubtaskCompletionCountByEngineerInRangeAsync(weekStartDt, weekEndDt, ct);
        var completedTasksByEngineer = await _tasks.GetCompletedTaskCountByEngineerInRangeAsync(weekStartDt, weekEndDt, ct);
        // Delivery-only (Task category), same as the PMO and Weekly reports' Hours column.
        var hoursThisWeek = await _timeEntries.GetDeliveryHoursByEngineerInRangeAsync(weekStart, weekEnd, null, ct);

        var tasksByAssignee = allActiveTasks
            .Where(t => t.AssigneeId.HasValue && t.Status.CountsAsActiveWorkload())
            .GroupBy(t => t.AssigneeId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Domain.Tasks.PulseTask>)g.ToList());

        // allActiveTasks already includes InQa — see GetPmoReportQuery's identical comment.
        var inQaTasksByAssignee = allActiveTasks
            .Where(t => t.AssigneeId.HasValue && t.Status == Domain.Tasks.TaskStatus.InQa)
            .GroupBy(t => t.AssigneeId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Domain.Tasks.PulseTask>)g.ToList());

        var blockersByAssignee = blockedTasks
            .Where(t => t.AssigneeId.HasValue)
            .GroupBy(t => t.AssigneeId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        var workloadByEngineer = await _assessor.AssessAsync(allEngineers, allActiveTasks, ct);

        // Executive/HeadOfPmo/ProjectManager/Accountant can never carry a delivery workload (see
        // CheckInExpected, "everyone doing delivery work") — without this they'd show up here at a
        // permanent 0 points/tasks whenever they fall inside the caller's scope.
        var deliveryRoles = CapabilityRegistry.All[CapabilityRegistry.CheckInExpected].AllowedRoles;
        // Same entry shape (and builder) as the PMO and Weekly reports, so all three show one table.
        var engineerEntries = allEngineers.Where(e => deliveryRoles.Contains(e.Role)).Select(engineer =>
            UtilizationEntryBuilder.Build(
                engineer, tasksByAssignee, inQaTasksByAssignee,
                checkInCounts, blockersByAssignee, hoursThisWeek, completedTasksByEngineer,
                subtasksCompletedByEngineer, workloadByEngineer)).ToList();

        var now = DateTime.UtcNow;
        var engineerNameById = allEngineers.ToDictionary(e => e.Id, e => e.Name);
        var relevantProjectIds = escalationCandidates.Select(t => t.ProjectId)
            .Concat(blockedTasks.Select(t => t.ProjectId))
            .Distinct()
            .ToList();
        var projectNameById = await _projects.GetNamesByIdsAsync(relevantProjectIds, ct);
        var projectCodeById = await _projects.GetCodesByIdsAsync(relevantProjectIds, ct);

        string? TaskKeyFor(Domain.Tasks.PulseTask t) =>
            projectCodeById.TryGetValue(t.ProjectId, out var code) ? $"{code}-{t.TaskNumber}" : null;

        var escalationEntries = escalationCandidates
            .Select(t =>
            {
                var level = EscalationLevelCalculator.Determine(t.DueDate!.Value, t.ActivatedAt, _thresholds, now);
                if (level is null) return null;
                engineerNameById.TryGetValue(t.AssigneeId ?? Guid.Empty, out var assigneeName);
                projectNameById.TryGetValue(t.ProjectId, out var projectName);
                return new EscalationReportEntry(t.Id, t.Title, t.AssigneeId, t.DueDate, level.Value.ToString(), assigneeName, projectName, TaskKeyFor(t));
            })
            .Where(e => e is not null)
            .Select(e => e!)
            .OrderBy(e => e.DueDate)
            .ToList();

        var blockedSince = await _tasks.GetBlockedSinceAsync(blockedTasks.Select(t => t.Id).ToList(), ct);
        var blockerEntries = blockedTasks
            .Select(t =>
            {
                engineerNameById.TryGetValue(t.AssigneeId ?? Guid.Empty, out var assigneeName);
                projectNameById.TryGetValue(t.ProjectId, out var projectName);
                var daysBlocked = ProjectHealthInputs.BlockedDays(t, blockedSince, now);
                return new BlockerReportEntry(t.Id, t.Title, t.AssigneeId, t.BlockerReason ?? string.Empty, assigneeName, projectName, TaskKeyFor(t), daysBlocked);
            })
            .ToList();

        return ServiceResult<LeadershipReportDto>.Ok(new LeadershipReportDto(
            weekStart.ToString("yyyy-MM-dd"),
            totalDeliveredPoints,
            previousWeekPoints,
            engineerEntries,
            escalationEntries,
            blockerEntries,
            IsCallerDepartmentScoped: deptEngineerIds is not null));
    }
}
