namespace Pulse.Application.Reports;

public record EngineerUtilizationEntry(
    Guid EngineerId,
    string Name,
    string Role,
    int ActiveTasks,
    int TotalPoints,
    int BaselinePoints,
    bool IsOverworked,
    int CheckInsThisWeek,
    int Blockers,
    decimal HoursLoggedThisWeek,
    int CompletedTasks = 0,
    // Additive, separate from ActiveTasks/TotalPoints (which stay Active+Blocked-only — the
    // correct scope for the overwork/capacity calculation they're shared with): a task awaiting
    // QA review still reflects real work done this period, so it shouldn't vanish from the report
    // the way it does from current-workload, but it also shouldn't quietly broaden the meaning of
    // a number used elsewhere for capacity planning.
    int TasksInQa = 0,
    int PointsInQa = 0,
    // Count of "subtask_completed" TaskHistory entries attributed to this engineer in the report's
    // window — subtasks have no points of their own, so this is a visibility signal, not a points
    // contribution.
    int SubtasksCompleted = 0,
    // The part of TotalPoints due within the engineer's baseline cycle (plus overdue/undated) — the number the
    // overwork signal compares with the baseline, and what IsOverworked is decided on. See EngineerWorkloadAssessor.
    int CyclePoints = 0);

public record TeamUtilizationDto(
    Guid TeamId,
    string TeamName,
    IReadOnlyList<EngineerUtilizationEntry> Engineers,
    int OverworkedCount,
    // Null when the team has nobody left to average over (empty, or every member is a role
    // excluded from the delivery roster) — never 0, which would misreport "nothing to measure" as
    // "a fully idle team."
    int? AvgLoadPct);

public record ProjectHealthDto(
    Guid ProjectId,
    string Name,
    int ActiveTasks,
    int BlockedTasks,
    int DoneThisSprint,
    int TotalTasks,
    int CompletionPct,
    int EscalationCount,
    string? ActiveSprintName,
    string Health, // "Healthy" | "AtRisk" | "Critical"
    decimal HoursLoggedThisWeek,
    int HighPriorityOpenTasks = 0,
    // Why Health is what it is, and what would clear it — see ProjectHealthCalculator.
    IReadOnlyList<HealthReasonDto>? Reasons = null,
    IReadOnlyList<string>? NextSteps = null,
    int OverdueTasks = 0,
    int DueSoonTasks = 0,
    // Weekly team report only: true when the counts cover just this team's own tasks on a project another
    // team owns, rather than the whole project.
    bool TeamSliceOnly = false);

public record SprintVelocityEntry(
    string SprintName,
    int? PlannedPoints,
    int DeliveredPoints);

public record TeamSprintVelocityDto(
    Guid TeamId,
    string TeamName,
    IReadOnlyList<SprintVelocityEntry> Sprints);

public record WeekComplianceDto(
    string WeekOf,
    int EngineerCount,
    int CheckedInCount,
    int CompliancePct);

public record TeamComplianceDto(
    Guid TeamId,
    string TeamName,
    IReadOnlyList<WeekComplianceDto> Weeks);

public record BlockerAgingDto(
    Guid TaskId,
    string Title,
    string? AssigneeName,
    string? ProjectName,
    string? Reason,
    int DaysBlocked,
    string? TaskKey = null);

public record PmoReportDto(
    string WeekOf,
    // The actual window "Points delivered" and every Hours column (Team Utilization, Project
    // Health) reflect — WeekOf's own Monday-Sunday week by default, but the caller's explicit
    // from/to when one was given. Check-in Compliance's 4-week table and everything else in the
    // report stay anchored to WeekOf's calendar week regardless of this range, since those are
    // inherently weekly/point-in-time concepts, not range-scoped ones — DeliveredFrom/DeliveredTo
    // exist so the frontend can label the headline correctly instead of implying it's WeekOf's
    // week when a custom range was requested.
    string DeliveredFrom,
    string DeliveredTo,
    int TotalDeliveredPoints,
    int PreviousWeekPoints,
    IReadOnlyList<TeamUtilizationDto> Teams,
    IReadOnlyList<ProjectHealthDto> Projects,
    IReadOnlyList<TeamSprintVelocityDto> SprintVelocity,
    IReadOnlyList<TeamComplianceDto> CheckInCompliance,
    IReadOnlyList<BlockerAgingDto> BlockerAging,
    // Org-wide average days from a task's creation to completion, over the same DeliveredFrom/
    // DeliveredTo window as TotalDeliveredPoints. Null when nothing completed in that window —
    // never 0, which would misreport "nothing to measure" as "instant turnaround". See
    // ITaskRepository.GetAvgCycleTimeDaysInRangeAsync.
    double? AvgCycleTimeDays = null,
    // Org-wide average hours from a PR approval request to its approval, among approvals that
    // landed in the same DeliveredFrom/DeliveredTo window. Null when nothing was approved in that
    // window. See ITaskRepository.GetAvgPrApprovalHoursInRangeAsync.
    double? AvgPrApprovalHours = null);
