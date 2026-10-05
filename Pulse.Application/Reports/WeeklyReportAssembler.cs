using Pulse.Application.Auth;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Escalations;
using Pulse.Application.Overwork;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;

namespace Pulse.Application.Reports;

/// <summary>
/// Shared digest-assembly logic for the weekly report query and its two write commands, so the
/// workstream/blocker/KPI aggregation (mirroring GetPmoReportHandler) isn't triplicated.
/// </summary>
internal static class WeeklyReportAssembler
{
    /// <summary>
    /// Resolves which team the caller may VIEW. HeadOfPmo/HeadOfProduct may target any team (teamId
    /// required). Department heads (HeadOfRnD/HeadOfDesign/HeadOfFunctional/HeadOfCoreBanking/HeadOfInfraDevOps) may target any team in
    /// their own department (teamId required), via the same CanAccessTeamAsync rule already used for
    /// the Leadership report/escalations. Everyone else (team lead, PM, project manager) is restricted
    /// to their own team only — this does NOT grant write access; Save/Submit enforce their own
    /// stricter "must be the team's own team lead" rule independently.
    /// </summary>
    public static async Task<ServiceResult<Guid>> ResolveTeamAsync(
        Guid callerId, string callerRole, Guid? requestedTeamId,
        IEngineerRepository engineers, IProjectAccessPolicy access, CancellationToken ct)
    {
        bool isPmo = callerRole is Roles.HeadOfPmo or Roles.HeadOfProduct;
        if (isPmo)
        {
            return requestedTeamId is null
                ? ServiceResult<Guid>.Fail("BUSINESS_RULE_VIOLATION", "teamId is required.")
                : ServiceResult<Guid>.Ok(requestedTeamId.Value);
        }

        bool isDepartmentHead = callerRole is Roles.HeadOfRnD or Roles.HeadOfDesign or Roles.HeadOfFunctional or Roles.HeadOfCoreBanking or Roles.HeadOfInfraDevOps;
        if (isDepartmentHead)
        {
            if (requestedTeamId is null)
                return ServiceResult<Guid>.Fail("BUSINESS_RULE_VIOLATION", "teamId is required.");

            return await access.CanAccessTeamAsync(requestedTeamId.Value, callerId, callerRole, ct)
                ? ServiceResult<Guid>.Ok(requestedTeamId.Value)
                : ServiceResult<Guid>.Fail("FORBIDDEN", "You may only access teams in your own department.");
        }

        var caller = await engineers.GetByIdAsync(callerId, ct);
        var callerTeamId = caller?.TeamId;
        if (callerTeamId is null)
            return ServiceResult<Guid>.Fail("NOT_FOUND", "Caller has no team.");

        if (requestedTeamId is not null && requestedTeamId != callerTeamId)
            return ServiceResult<Guid>.Fail("FORBIDDEN", "You may only access your own team's weekly report.");

        return ServiceResult<Guid>.Ok(callerTeamId.Value);
    }

    /// <summary>
    /// Authorizes a WRITE (save draft / submit) against a specific, already-known team. Scoped
    /// identically to <see cref="ResolveTeamAsync"/>'s read access: HeadOfPmo/HeadOfProduct unscoped,
    /// HeadOfRnD/HeadOfDesign/HeadOfFunctional/HeadOfCoreBanking/HeadOfInfraDevOps restricted to their own department, TeamLead restricted
    /// to their own team. Everyone else (ProjectManager, product_manager, etc.) is forbidden — they
    /// don't get elevated read access either, so there's no reason to grant elevated write.
    /// </summary>
    public static async Task<ServiceResult<Guid>> AuthorizeWriteAsync(
        Guid callerId, string callerRole, Guid teamId,
        IEngineerRepository engineers, IProjectAccessPolicy access, CancellationToken ct)
    {
        if (callerRole is Roles.HeadOfPmo or Roles.HeadOfProduct)
            return ServiceResult<Guid>.Ok(teamId);

        if (callerRole is Roles.HeadOfRnD or Roles.HeadOfDesign or Roles.HeadOfFunctional or Roles.HeadOfCoreBanking or Roles.HeadOfInfraDevOps)
        {
            return await access.CanAccessTeamAsync(teamId, callerId, callerRole, ct)
                ? ServiceResult<Guid>.Ok(teamId)
                : ServiceResult<Guid>.Fail("FORBIDDEN", "You may only edit weekly reports for teams in your own department.");
        }

        if (callerRole == Roles.TeamLead)
        {
            var caller = await engineers.GetByIdAsync(callerId, ct);
            return caller?.TeamId == teamId
                ? ServiceResult<Guid>.Ok(teamId)
                : ServiceResult<Guid>.Fail("FORBIDDEN", "You may only edit your own team's weekly report.");
        }

        return ServiceResult<Guid>.Fail("FORBIDDEN", "Only the team's team lead or a department/PMO head may edit its weekly report.");
    }

    public static async Task<ServiceResult<WeeklyReportDto>> BuildAsync(
        Guid teamId,
        DateOnly? weekOfParam,
        ITeamRepository teams,
        IEngineerRepository engineers,
        IProjectRepository projects,
        ITaskRepository tasks,
        ICheckInRepository checkIns,
        ITimeEntryRepository timeEntries,
        IWeeklyReportRepository weeklyReports,
        EngineerWorkloadAssessor assessor,
        OverworkThresholds thresholds,
        CancellationToken ct,
        Domain.Reports.WeeklyReport? preloadedReport = null)
    {
        var team = await teams.GetByIdAsync(teamId, ct);
        if (team is null)
            return ServiceResult<WeeklyReportDto>.Fail("NOT_FOUND", "Team not found.");

        // 1. Date window — same Monday-normalization as GetPmoReportHandler/GetLeadershipReportHandler
        var today = weekOfParam ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var weekStart = WeekOf.Monday(today);
        var weekEnd = weekStart.AddDays(6);
        var sevenDaysAgo = today.AddDays(-6);
        var weekStartDt = weekStart.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var weekEndDt = weekEnd.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc);

        // 2. Load data sequentially — repositories share one scoped DbContext.
        var allEngineers = await engineers.ListActiveAsync(ct);
        var allProjects = await projects.ListActiveAsync(ct);
        var allActiveTasks = await tasks.GetAllActiveAsync(ct);
        var blockedTasks = await tasks.GetBlockedTasksAsync(ct);
        var escalationLookahead = (int)Math.Ceiling(thresholds.EscalationT3Days);
        var escalationCandidates = await tasks.GetEscalationCandidatesAsync(escalationLookahead, ct);
        var checkInCounts = await checkIns.GetCheckInCountByDateRangeAsync(sevenDaysAgo, today, ct);
        var checkedInByProject = await checkIns.GetCheckedInEngineersByProjectAsync(weekStart, weekEnd, ct);
        // Delivery-only (Task category) — see ITimeEntryRepository.GetDeliveryHoursByEngineerInRangeAsync.
        var hoursThisWeek = await timeEntries.GetDeliveryHoursByEngineerInRangeAsync(weekStart, weekEnd, null, ct);
        var completedTasksByEngineer = await tasks.GetCompletedTaskCountByEngineerInRangeAsync(weekStartDt, weekEndDt, ct);
        var subtasksCompletedByEngineer = await tasks.GetSubtaskCompletionCountByEngineerInRangeAsync(weekStartDt, weekEndDt, ct);
        var existingReport = preloadedReport ?? await weeklyReports.GetByTeamAndWeekAsync(teamId, weekStart, ct);

        // 3. Scope to this team. A project counts as this team's workstream if the team owns it OR
        //    at least one of the team's own engineers has an active task in it — cross-team loaned
        //    work is common and shouldn't silently vanish from the weekly report just because the
        //    project itself is owned elsewhere.
        var teamEngineers = allEngineers.Where(e => e.TeamId == teamId).ToList();
        var teamEngineerIds = teamEngineers.Select(e => e.Id).ToHashSet();
        var teamEngineerIdList = teamEngineerIds.ToList();
        var engineerNameById = allEngineers.ToDictionary(e => e.Id, e => e.Name);
        // Executive/HeadOfPmo/ProjectManager/Accountant can never carry a delivery workload (see
        // CheckInExpected, "everyone doing delivery work") — used below to keep them out of Team
        // Utilization and Check-in Compliance specifically (a permanent 0-points/non-compliant
        // entry otherwise), without narrowing the role-agnostic project/points scoping above.
        var deliveryRoles = CapabilityRegistry.All[CapabilityRegistry.CheckInExpected].AllowedRoles;
        var deliveryTeamEngineers = teamEngineers.Where(e => deliveryRoles.Contains(e.Role)).ToList();

        var ownedProjectIds = allProjects.Where(p => p.OwnerTeamId == teamId).Select(p => p.Id).ToHashSet();
        var crossTeamProjectIds = allActiveTasks
            .Where(t => t.AssigneeId.HasValue && teamEngineerIds.Contains(t.AssigneeId.Value))
            .Select(t => t.ProjectId)
            .ToHashSet();
        var scopedProjectIds = ownedProjectIds.Union(crossTeamProjectIds).ToHashSet();
        var scopedProjects = allProjects.Where(p => scopedProjectIds.Contains(p.Id)).ToList();

        var tasksByAssignee = allActiveTasks
            .Where(t => t.AssigneeId.HasValue && t.Status.CountsAsActiveWorkload())
            .GroupBy(t => t.AssigneeId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Domain.Tasks.PulseTask>)g.ToList());
        // allActiveTasks already includes InQa — see GetPmoReportQuery's identical comment.
        var inQaTasksByAssignee = allActiveTasks
            .Where(t => t.AssigneeId.HasValue && t.Status == Domain.Tasks.TaskStatus.InQa)
            .GroupBy(t => t.AssigneeId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Domain.Tasks.PulseTask>)g.ToList());
        // Restricted to this team's own engineers (not just anyone active on a scoped project) so
        // per-workstream check-in coverage below stays a coverage rate for this team, not a mix of
        // whichever teams happen to share the project.
        var activeAssigneesByProject = allActiveTasks
            .Where(t => scopedProjectIds.Contains(t.ProjectId) && t.AssigneeId.HasValue
                     && teamEngineerIds.Contains(t.AssigneeId.Value) && t.Status.CountsAsActiveWorkload())
            .GroupBy(t => t.ProjectId)
            .ToDictionary(g => g.Key, g => g.Select(t => t.AssigneeId!.Value).ToHashSet());
        var blockersByAssignee = blockedTasks
            .Where(t => t.AssigneeId.HasValue)
            .GroupBy(t => t.AssigneeId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());
        var workloadByEngineer = await assessor.AssessAsync(allEngineers, allActiveTasks, ct);

        // 4. Escalations per project (shared math with GetPmoReportHandler/GetLeadershipReportHandler),
        //    scoped to this team's projects. For an owned project every escalating task counts (the
        //    team owns the whole deliverable); for a cross-team project only escalating tasks assigned
        //    to this team's own engineers count, so another team's escalation on a shared project
        //    doesn't show up here too.
        var now = DateTime.UtcNow;
        var escalationsByProject = new Dictionary<Guid, int>();
        foreach (var t in escalationCandidates)
        {
            if (t.DueDate is null || !scopedProjectIds.Contains(t.ProjectId)) continue;
            if (!ownedProjectIds.Contains(t.ProjectId) && !(t.AssigneeId.HasValue && teamEngineerIds.Contains(t.AssigneeId.Value)))
                continue;
            var level = EscalationLevelCalculator.Determine(t.DueDate.Value, t.ActivatedAt, thresholds, now);

            if (level is not null)
                escalationsByProject[t.ProjectId] = escalationsByProject.GetValueOrDefault(t.ProjectId) + 1;
        }

        // 5. Team utilization
        var utilizationEntries = deliveryTeamEngineers.Select(engineer => UtilizationEntryBuilder.Build(
            engineer, tasksByAssignee, inQaTasksByAssignee,
            checkInCounts, blockersByAssignee, hoursThisWeek, completedTasksByEngineer,
            subtasksCompletedByEngineer, workloadByEngineer)).ToList();

        var overworkedCount = utilizationEntries.Count(e => e.IsOverworked);
        // Null (not 0) when nobody delivery-eligible is left on the team — see TeamUtilizationDto.AvgLoadPct.
        int? avgLoadPct = utilizationEntries.Count > 0
            ? (int)Math.Round(utilizationEntries.Average(e => (double)e.TotalPoints / e.BaselinePoints * 100))
            : null;
        var utilization = new TeamUtilizationDto(teamId, team.Name, utilizationEntries, overworkedCount, avgLoadPct);

        // Overdue / due-soon / blocked per project, scoped like the counts below (an owned project is whole;
        // a cross-team project is only this team's own slice), so each status can explain itself.
        bool InTeamSlice(Domain.Tasks.PulseTask t) =>
            scopedProjectIds.Contains(t.ProjectId)
            && (ownedProjectIds.Contains(t.ProjectId) || (t.AssigneeId.HasValue && teamEngineerIds.Contains(t.AssigneeId.Value)));
        var healthCodeByProject = scopedProjects.ToDictionary(p => p.Id, p => p.Code);
        var healthBlockedSince = await tasks.GetBlockedSinceAsync(blockedTasks.Select(t => t.Id).ToList(), ct);
        var (overdueByProject, dueSoonByProject) = ProjectHealthInputs.FromEscalations(
            escalationCandidates, InTeamSlice, healthCodeByProject, engineerNameById, thresholds, now);
        var blockedByProject = ProjectHealthInputs.FromBlocked(
            blockedTasks, InTeamSlice, healthBlockedSince, healthCodeByProject, engineerNameById, now);

        // 6. Workstream / project health. An owned project shows its true, whole-project counts (the
        //    team owns the whole deliverable); a cross-team project shows only this team's own slice
        //    of it, so a shared project doesn't appear to belong entirely to every team touching it.
        //    Hours follow the same "this team's own slice" spirit — scoped to this team's own
        //    engineers regardless of whether the project is owned or cross-team.
        var hoursByProjectThisWeek = await timeEntries.GetHoursByProjectInRangeAsync(weekStart, weekEnd, teamEngineerIdList, ct);
        var workstreams = new List<ProjectHealthDto>();
        var checkInCoverage = new List<WorkstreamCheckInDto>();
        foreach (var project in scopedProjects)
        {
            var counts = ownedProjectIds.Contains(project.Id)
                ? await tasks.GetProjectTaskCountsAsync(project.Id, ct)
                : await tasks.GetProjectTaskCountsAsync(project.Id, teamEngineerIdList, ct);
            var escalCount = escalationsByProject.GetValueOrDefault(project.Id, 0);
            var assessment = ProjectHealthCalculator.Assess(
                overdueByProject.GetValueOrDefault(project.Id) ?? [],
                dueSoonByProject.GetValueOrDefault(project.Id) ?? [],
                blockedByProject.GetValueOrDefault(project.Id) ?? [],
                thresholds);
            var completionPct = counts.TotalCount > 0
                ? (int)Math.Round((double)counts.DoneCount / counts.TotalCount * 100)
                : 0;

            workstreams.Add(new ProjectHealthDto(
                project.Id, project.Name,
                counts.ActiveCount, counts.BlockedCount, counts.DoneThisSprintCount,
                counts.TotalCount, completionPct,
                escalCount, null, assessment.Health,
                hoursByProjectThisWeek.GetValueOrDefault(project.Id, 0m),
                0,
                assessment.Reasons, assessment.NextSteps,
                (overdueByProject.GetValueOrDefault(project.Id)?.Count) ?? 0,
                (dueSoonByProject.GetValueOrDefault(project.Id)?.Count) ?? 0,
                TeamSliceOnly: !ownedProjectIds.Contains(project.Id)));

            // Coverage = how many of the engineers actively working on this project this week also
            // logged a check-in tagged to it — additive to (not a replacement for) the team-wide,
            // project-agnostic Check-in Compliance KPI below.
            var expected = activeAssigneesByProject.GetValueOrDefault(project.Id, new HashSet<Guid>());
            var checkedIn = checkedInByProject.GetValueOrDefault(project.Id, new HashSet<Guid>());
            var coveredCount = expected.Intersect(checkedIn).Count();
            checkInCoverage.Add(new WorkstreamCheckInDto(project.Id, expected.Count, coveredCount,
                expected.Count > 0 ? (int)Math.Round((double)coveredCount / expected.Count * 100) : 0));
        }

        // 7. Risks & blockers
        var blockers = blockedTasks
            .Where(t => scopedProjectIds.Contains(t.ProjectId))
            .Select(t =>
            {
                engineerNameById.TryGetValue(t.AssigneeId ?? Guid.Empty, out var assigneeName);
                var project = scopedProjects.FirstOrDefault(p => p.Id == t.ProjectId);
                var days = ProjectHealthInputs.BlockedDays(t, healthBlockedSince, now);
                var taskKey = project is not null ? $"{project.Code}-{t.TaskNumber}" : null;
                return new BlockerAgingDto(t.Id, t.Title, assigneeName, project?.Name, t.BlockerReason, days, taskKey);
            })
            .OrderByDescending(b => b.DaysBlocked)
            .ToList();

        // 8. Check-in compliance (last 4 weeks)
        var weekEntries = new List<WeekComplianceDto>();
        var complianceEngineerIds = deliveryTeamEngineers.Select(e => e.Id).ToHashSet();
        var teamEngCount = complianceEngineerIds.Count;
        for (int w = 3; w >= 0; w--)
        {
            var wStart = weekStart.AddDays(-7 * w);
            var wEnd = wStart.AddDays(6);
            var weekCheckIns = await checkIns.GetCheckInCountByDateRangeAsync(wStart, wEnd, ct);
            var checkedIn = teamEngCount == 0 ? 0 : weekCheckIns.Keys.Count(id => complianceEngineerIds.Contains(id) && weekCheckIns[id] > 0);
            var pct = teamEngCount == 0 ? 0 : (int)Math.Round((double)checkedIn / teamEngCount * 100);
            weekEntries.Add(new WeekComplianceDto(wStart.ToString("yyyy-MM-dd"), teamEngCount, checkedIn, pct));
        }
        var compliance = new TeamComplianceDto(teamId, team.Name, weekEntries);

        // 9. Delivered points, scoped to this team's own engineers — NOT the scoped project list,
        //    which now includes cross-team projects and would double-count another team's completed
        //    work on a shared project if used here.
        var prevStartDt = weekStartDt.AddDays(-7);
        var prevEndDt = weekStartDt.AddTicks(-1);
        var totalDelivered = await tasks.GetDeliveredPointsInRangeByAssigneesAsync(weekStartDt, weekEndDt, teamEngineerIdList, ct);
        var prevWeekPoints = await tasks.GetDeliveredPointsInRangeByAssigneesAsync(prevStartDt, prevEndDt, teamEngineerIdList, ct);

        // 10. Narrative / sign-off fields
        var isNew = existingReport is null;
        string? submittedByName = null;
        if (existingReport?.SubmittedById is { } submittedById)
            engineerNameById.TryGetValue(submittedById, out submittedByName);

        return ServiceResult<WeeklyReportDto>.Ok(new WeeklyReportDto(
            teamId, team.Name, weekStart.ToString("yyyy-MM-dd"),
            totalDelivered, prevWeekPoints,
            utilization, workstreams, blockers, compliance, checkInCoverage,
            existingReport?.ExecutiveSummary ?? string.Empty,
            existingReport?.KeyAccomplishments ?? string.Empty,
            existingReport?.PlannedNextWeek ?? string.Empty,
            existingReport?.ResourcingNotes ?? string.Empty,
            WeeklyReportNarrativeGenerator.ExecutiveSummary(totalDelivered, prevWeekPoints, workstreams, blockers, utilization),
            WeeklyReportNarrativeGenerator.KeyAccomplishments(workstreams),
            WeeklyReportNarrativeGenerator.PlannedNextWeek(workstreams, blockers),
            WeeklyReportNarrativeGenerator.ResourcingNotes(utilization),
            existingReport?.SubmittedById, submittedByName, existingReport?.SubmittedAt, existingReport?.UpdatedAt,
            isNew));
    }
}
