using Pulse.Domain.Tasks;

namespace Pulse.Application.Common.Interfaces;

public record ProjectTaskCounts(
    int ActiveCount,
    int BlockedCount,
    int DoneThisSprintCount,
    int TotalCount,
    int DoneCount,
    string? LastBlockerTitle,
    Guid? ActiveSprintId,
    int HighPriorityOpenCount = 0);

/// <summary>A task considered for archiving. <see cref="TouchedSince"/> is true when something has happened to it since the baseline began,
/// which marks it as live work rather than pilot-era leftovers.</summary>
public record ArchiveCandidate(
    Guid Id, Guid ProjectId, int TaskNumber, string Title, Domain.Tasks.TaskStatus Status,
    Guid? AssigneeId, Guid? ParentTaskId, DateTime CreatedAt, bool TouchedSince);

/// <summary>Raw counts behind an engineer's performance metrics for a date range — see PerformanceMetricsCalculator
/// for how these turn into rates. TasksSentToQa/TasksQaRejected are keyed off "sent to QA within range",
/// not "resolved within range", so a rejection can be counted against a task sent to QA slightly before
/// the range started; acceptable for a rolling-window metric. TasksCompletedOnTime/TasksWithDueDate are a
/// matched pair: a completed task with no due date has no "on time" to measure, so it's excluded from
/// both the numerator and the denominator rather than counted as trivially on-time — "on-time rate" is
/// TasksCompletedOnTime / TasksWithDueDate, not / TasksCompleted.</summary>
public record TaskPerformanceStats(
    int DeliveredPoints,
    int TasksCompleted,
    int TasksWithDueDate,
    int TasksCompletedOnTime,
    double? AvgCycleTimeDays,
    int TasksSentToQa,
    int TasksQaRejected);

/// <summary>One TaskHistory entry joined against its task's title, for a project-scoped activity feed.</summary>
public record ProjectActivityEntry(
    Guid Id,
    Guid TaskId,
    string TaskTitle,
    string Field,
    string? OldValue,
    string? NewValue,
    Guid ActorId,
    DateTime ChangedAt,
    string? Context,
    string? Reason = null);

public interface ITaskRepository
{
    Task<PulseTask?> GetByIdAsync(Guid id, CancellationToken ct = default);
    /// <summary>Also finds an archived task (to show it read-only, restore it, or archive it).</summary>
    Task<PulseTask?> GetByIdIncludingArchivedAsync(Guid id, CancellationToken ct = default);

    /// <summary>Tasks created before <paramref name="createdBefore"/> that are not archived and not in a personal project, optionally limited to some
    /// projects, each flagged when anything has happened to it since (<paramref name="touchedSince"/>: a history entry or comment at or after it, or
    /// time logged on or after <paramref name="touchedSinceDate"/>).</summary>
    Task<IReadOnlyList<ArchiveCandidate>> FindArchiveCandidatesAsync(
        DateTime createdBefore, DateTime touchedSince, DateOnly touchedSinceDate, IReadOnlyList<Guid>? projectIds, CancellationToken ct = default);

    /// <summary>The QA tasks of the given parents whatever their own creation date (a QA task is created when its parent is sent to QA, which can be
    /// after the cutoff), with the same touched flag.</summary>
    Task<IReadOnlyList<ArchiveCandidate>> FindQaTasksOfAsync(
        IReadOnlyList<Guid> parentIds, DateTime touchedSince, DateOnly touchedSinceDate, CancellationToken ct = default);

    /// <summary>A task and its QA tasks, archived or not (to restore them together).</summary>
    Task<IReadOnlyList<PulseTask>> GetGroupIncludingArchivedAsync(Guid rootTaskId, CancellationToken ct = default);

    Task<(IReadOnlyList<PulseTask> Items, int Total)> ListArchivedAsync(
        Guid? projectId, string? search, int skip, int take, CancellationToken ct = default);
    Task<IReadOnlyList<PulseTask>> GetByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default);
    Task<(IReadOnlyList<PulseTask> Items, string? NextCursor)> ListAsync(
        Guid? projectId, Guid? assigneeId, Pulse.Domain.Tasks.TaskStatus? status,
        Pulse.Domain.Tasks.TaskType? taskType, Guid? sprintId, Guid? epicId, int limit, string? cursor,
        bool noSprint = false, string? title = null, IReadOnlyList<Guid>? departmentAssigneeIds = null,
        IReadOnlyList<Guid>? departmentSprintIds = null, IReadOnlyList<Guid>? departmentProjectIds = null,
        Discipline? discipline = null, bool excludeDone = false, IReadOnlyList<Guid>? excludeAssigneeIds = null,
        bool noAssignee = false, string? sortBy = null, string? sortDirection = null, CancellationToken ct = default);
    Task<IReadOnlyList<PulseTask>> GetActiveByAssigneeAsync(Guid assigneeId, CancellationToken ct = default);
    /// <summary>Tasks this engineer most recently did backend work on (BackendAssigneeId) that
    /// aren't currently assigned to them — i.e. handed off to Frontend and not yet handed back.
    /// See <see cref="Pulse.Application.Tasks.Queries.ListMyFrontendHandoffsQuery"/>.</summary>
    Task<IReadOnlyList<PulseTask>> GetHandedOffByAsync(Guid backendAssigneeId, CancellationToken ct = default);
    Task<IReadOnlyList<PulseTask>> GetAllActiveAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PulseTask>> GetBlockedTasksAsync(CancellationToken ct = default);
    /// <summary>When each of these tasks most recently became Blocked (latest status -> Blocked history
    /// entry). A task with no such entry is simply absent — callers fall back to another timestamp.</summary>
    Task<IReadOnlyDictionary<Guid, DateTime>> GetBlockedSinceAsync(IReadOnlyList<Guid> taskIds, CancellationToken ct = default);
    Task<IReadOnlyList<PulseTask>> GetEscalationCandidatesAsync(int daysLookahead = 3, CancellationToken ct = default);
    /// <summary>The same candidates, but only tasks in personal projects (excluded from the method above).
    /// Used solely by EscalationScanner, which reminds the owner and nobody else.</summary>
    Task<IReadOnlyList<PulseTask>> GetPersonalEscalationCandidatesAsync(int daysLookahead = 3, CancellationToken ct = default);
    Task<IReadOnlyList<PulseTask>> GetBySprintAsync(Guid sprintId, CancellationToken ct = default);
    Task<IReadOnlyList<PulseTask>> GetByProjectAsync(Guid projectId, CancellationToken ct = default);
    /// <summary>Batched form of <see cref="GetBySprintAsync"/> — one query for every sprint, grouped by sprint ID.
    /// A sprint with no tasks is simply absent from the result.</summary>
    Task<Dictionary<Guid, IReadOnlyList<PulseTask>>> GetBySprintIdsAsync(IReadOnlyList<Guid> sprintIds, CancellationToken ct = default);

    /// <summary>Performance stats for one engineer over a date range, optionally scoped to a single project.</summary>
    Task<TaskPerformanceStats> GetPerformanceStatsAsync(
        Guid assigneeId, DateTime from, DateTime to, Guid? projectId = null, CancellationToken ct = default);

    /// <summary>Distinct project ids of the tasks in a sprint — used to evaluate sprint visibility without loading task entities.</summary>
    Task<IReadOnlyList<Guid>> GetProjectIdsBySprintAsync(Guid sprintId, CancellationToken ct = default);

    /// <summary>True if the sprint contains at least one task assigned to the given engineer.</summary>
    Task<bool> IsAssignedInSprintAsync(Guid sprintId, Guid engineerId, CancellationToken ct = default);
    /// <summary>True if the engineer has at least one task assigned to them in the given project — the same
    /// "assigned work" leg of ListMyProjectsAsync's definition of "my projects", used to keep the timer's
    /// unclaimed-work browsing/claiming consistent with what /projects/mine already shows the caller,
    /// since ProjectAccessPolicy.CanAccessProjectAsync alone (membership or owning-team) is narrower.</summary>
    Task<bool> HasAssignedTaskInProjectAsync(Guid engineerId, Guid projectId, CancellationToken ct = default);
    /// <summary>Returns total and completed task counts keyed by epic ID. Missing epics return (0,0).</summary>
    Task<Dictionary<Guid, (int Total, int Completed)>> GetTaskProgressByEpicsAsync(IReadOnlyList<Guid> epicIds, CancellationToken ct = default);
    /// <summary>Sums the points of tasks marked Done within the given UTC time range, identified by TaskHistory entries.</summary>
    Task<int> GetDeliveredPointsInRangeAsync(DateTime from, DateTime to, CancellationToken ct = default);
    /// <summary>Same as <see cref="GetDeliveredPointsInRangeAsync(DateTime,DateTime,CancellationToken)"/>, scoped to the given projects.</summary>
    Task<int> GetDeliveredPointsInRangeAsync(DateTime from, DateTime to, IReadOnlyList<Guid> projectIds, CancellationToken ct = default);
    /// <summary>Same as <see cref="GetDeliveredPointsInRangeAsync(DateTime,DateTime,CancellationToken)"/>, scoped to tasks
    /// assigned to the given engineers — for a team's weekly report, where a project-scoped sum would
    /// wrongly include other teams' completed work on a shared/cross-team project.</summary>
    Task<int> GetDeliveredPointsInRangeByAssigneesAsync(DateTime from, DateTime to, IReadOnlyList<Guid> assigneeIds, CancellationToken ct = default);
    /// <summary>Org-wide average whole days from creation to completion (see
    /// <see cref="Pulse.Application.Performance.PerformanceMetricsCalculator.CycleTimeDays"/>) across tasks whose
    /// latest Done transition falls in the given UTC range — same TaskHistory-based detection and QA-sub-task
    /// exclusion as <see cref="GetDeliveredPointsInRangeAsync(DateTime,DateTime,CancellationToken)"/>. Null when
    /// nothing completed in range, never 0 (which would misreport "nothing to measure" as "instant turnaround").</summary>
    Task<double?> GetAvgCycleTimeDaysInRangeAsync(DateTime from, DateTime to, CancellationToken ct = default);
    /// <summary>Same as <see cref="GetAvgCycleTimeDaysInRangeAsync(DateTime,DateTime,CancellationToken)"/>, scoped to the given projects.</summary>
    Task<double?> GetAvgCycleTimeDaysInRangeAsync(DateTime from, DateTime to, IReadOnlyList<Guid> projectIds, CancellationToken ct = default);
    /// <summary>Org-wide average hours from a PR approval request (<see cref="Domain.Tasks.PulseTask.RequestPrApproval"/>)
    /// to its approval (<see cref="Domain.Tasks.PulseTask.ApprovePrApproval"/>), among approvals whose
    /// TaskHistory timestamp falls in the given UTC range. Each approval pairs with the most recent request
    /// that preceded it — mirroring the live domain model, where only one request is ever pending at a time
    /// and approving always resolves whichever one is currently pending — so a rejected-and-abandoned request
    /// contributes no duration rather than being treated as an error. Null when nothing was approved in range.</summary>
    Task<double?> GetAvgPrApprovalHoursInRangeAsync(DateTime from, DateTime to, CancellationToken ct = default);
    /// <summary>Same as <see cref="GetAvgPrApprovalHoursInRangeAsync(DateTime,DateTime,CancellationToken)"/>, scoped to the given projects.</summary>
    Task<double?> GetAvgPrApprovalHoursInRangeAsync(DateTime from, DateTime to, IReadOnlyList<Guid> projectIds, CancellationToken ct = default);
    /// <summary>Counts of tasks marked Done within the given UTC time range, keyed by current assignee —
    /// same TaskHistory-based detection and QA-sub-task exclusion as <see cref="GetDeliveredPointsInRangeAsync(DateTime,DateTime,CancellationToken)"/>,
    /// counting tasks per engineer instead of summing an org-wide points total.</summary>
    Task<IReadOnlyDictionary<Guid, int>> GetCompletedTaskCountByEngineerInRangeAsync(DateTime from, DateTime to, CancellationToken ct = default);
    /// <summary>Counts of subtasks completed within the given UTC time range, keyed by whoever
    /// checked them off — reads the "subtask_completed" TaskHistory entries PulseTask.RecordSubtaskCompleted
    /// writes on the parent task, since Subtask has no history of its own.</summary>
    Task<IReadOnlyDictionary<Guid, int>> GetSubtaskCompletionCountByEngineerInRangeAsync(DateTime from, DateTime to, CancellationToken ct = default);
    /// <summary>Title/key search. Personal tasks (see Project.PersonalOwnerId) are left out unless they
    /// belong to <paramref name="viewerId"/> — people find their own to-dos, nobody finds anyone else's.</summary>
    Task<IReadOnlyList<PulseTask>> SearchAsync(string q, int limit, Guid viewerId, CancellationToken ct = default);
    /// <summary>Returns (date, points) pairs for tasks in the sprint that were marked Done, using task history.</summary>
    Task<IReadOnlyList<(DateOnly Date, int Points)>> GetBurndownDataAsync(Guid sprintId, CancellationToken ct = default);
    /// <summary>Returns task counts and the active sprint ID for a project in one efficient query.</summary>
    Task<ProjectTaskCounts> GetProjectTaskCountsAsync(Guid projectId, CancellationToken ct = default);
    /// <summary>Same as <see cref="GetProjectTaskCountsAsync(Guid,CancellationToken)"/>, but counts only
    /// tasks assigned to the given engineers — for a cross-team project on a team's weekly report, where
    /// the whole project's counts would wrongly include other teams' work on it. ActiveSprintId is still
    /// resolved from every task in the project (a project-wide fact), not just the given engineers'.</summary>
    Task<ProjectTaskCounts> GetProjectTaskCountsAsync(Guid projectId, IReadOnlyList<Guid> assigneeIds, CancellationToken ct = default);
    /// <summary>Batched form of <see cref="GetProjectTaskCountsAsync"/> — two round trips total (tasks, then active
    /// sprints) across every given project, instead of two per project.</summary>
    Task<Dictionary<Guid, ProjectTaskCounts>> GetProjectTaskCountsBatchAsync(IReadOnlyList<Guid> projectIds, CancellationToken ct = default);
    /// <summary>Returns a page of the most recent task-history entries across every task in a project,
    /// newest first, keyset-paginated by an opaque cursor (see ProjectActivityCursor).</summary>
    Task<(IReadOnlyList<ProjectActivityEntry> Items, string? NextCursor)> GetRecentActivityByProjectAsync(Guid projectId, int limit, string? cursor, CancellationToken ct = default);
    /// <summary>Returns delivered story points grouped by week (Monday) for the 6-week rolling window ending
    /// today, for tasks assigned to the given engineer — the per-project analogue of
    /// <see cref="IProjectRepository.GetWeeklyThroughputAsync"/>.</summary>
    Task<IReadOnlyList<WeeklyThroughputPoint>> GetWeeklyThroughputByAssigneeAsync(Guid engineerId, CancellationToken ct = default);
    /// <summary>Returns delivered story points grouped by week (Monday) for the 6-week rolling window ending
    /// today, for tasks assigned to any engineer on the given team — the team-level analogue of
    /// <see cref="GetWeeklyThroughputByAssigneeAsync"/>.</summary>
    Task<IReadOnlyList<WeeklyThroughputPoint>> GetWeeklyThroughputByTeamAsync(Guid teamId, CancellationToken ct = default);
    /// <summary>Returns delivered story points grouped by week (Monday) for the 6-week rolling window ending
    /// today, across every team — the org-wide analogue of <see cref="GetWeeklyThroughputByTeamAsync"/>.</summary>
    Task<IReadOnlyList<WeeklyThroughputPoint>> GetWeeklyThroughputOrgWideAsync(CancellationToken ct = default);
    /// <summary>Next sequential task number for a project — one past the current max, or 1 if the
    /// project has no tasks yet. See <see cref="Pulse.Application.Tasks.TaskNumberAllocator"/> for
    /// the batch-safe wrapper most callers should use instead of calling this per task.</summary>
    Task<int> GetNextTaskNumberAsync(Guid projectId, CancellationToken ct = default);
    Task AddAsync(PulseTask task, CancellationToken ct = default);
    Task DeleteAsync(PulseTask task, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
