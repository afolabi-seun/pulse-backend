using System.Globalization;
using System.Text.RegularExpressions;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Performance;
using Pulse.Application.Projects.Queries;
using Pulse.Application.Tasks;
using Pulse.Domain.Sprints;
using Pulse.Domain.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.Persistence.Repositories;

public class TaskRepository : ITaskRepository
{
    private readonly PulseDbContext _db;

    public TaskRepository(PulseDbContext db) => _db = db;

    private static readonly Regex TaskKeyPattern = new(@"^([A-Za-z]+)-(\d+)$", RegexOptions.Compiled);

    /// <summary>Recognizes a task key like "CIB-16" (project code + task number) or a bare task
    /// number like "16" (matched against any project), so search/title filtering can find a task
    /// by key as well as by title text. Returns (null, null) when <paramref name="q"/> doesn't
    /// look like either shape.</summary>
    private static (string? Code, int? Number) ParseTaskKey(string q)
    {
        var trimmed = q.Trim();
        var m = TaskKeyPattern.Match(trimmed);
        if (m.Success) return (m.Groups[1].Value, int.Parse(m.Groups[2].Value));
        return int.TryParse(trimmed, out var n) ? (null, n) : (null, null);
    }

    /// <summary>Drops tasks that live in a personal-tasks project (see Project.PersonalOwnerId). Applied to
    /// every org-wide enumeration — escalations, blocked/active sweeps, search, the unfiltered task list —
    /// so one person's private to-dos never escalate to, or show up for, anybody else.</summary>
    private IQueryable<PulseTask> WithoutPersonal(IQueryable<PulseTask> query) =>
        query.Where(t => !_db.Projects.Any(p => p.Id == t.ProjectId && p.PersonalOwnerId != null));

    public async Task<PulseTask?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.Tasks.Include(t => t.History).FirstOrDefaultAsync(t => t.Id == id, ct);

    // No History include — this backs bulk title lookups (e.g. time entries), which never need it.
    public async Task<IReadOnlyList<PulseTask>> GetByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default) =>
        await _db.Tasks.Where(t => ids.Contains(t.Id)).ToListAsync(ct);

    public async Task<(IReadOnlyList<PulseTask> Items, string? NextCursor)> ListAsync(
        Guid? projectId, Guid? assigneeId, Domain.Tasks.TaskStatus? status,
        Domain.Tasks.TaskType? taskType, Guid? sprintId, Guid? epicId, int limit, string? cursor,
        bool noSprint = false, string? title = null, IReadOnlyList<Guid>? departmentAssigneeIds = null,
        IReadOnlyList<Guid>? departmentSprintIds = null, IReadOnlyList<Guid>? departmentProjectIds = null,
        Domain.Tasks.Discipline? discipline = null, bool excludeDone = false, IReadOnlyList<Guid>? excludeAssigneeIds = null,
        bool noAssignee = false, string? sortBy = null, string? sortDirection = null, CancellationToken ct = default)
    {
        var query = _db.Tasks.AsQueryable();

        // An org-wide listing (no project, no assignee) never includes personal tasks; asking for a
        // specific project or a specific person's tasks is how their owner reaches them.
        if (!projectId.HasValue && !assigneeId.HasValue)
            query = WithoutPersonal(query);

        if (projectId.HasValue)  query = query.Where(t => t.ProjectId == projectId.Value);
        if (assigneeId.HasValue) query = query.Where(t => t.AssigneeId == assigneeId.Value);
        if (noAssignee)          query = query.Where(t => t.AssigneeId == null);
        if (title is not null)
        {
            var (titleKeyCode, titleKeyNumber) = ParseTaskKey(title);
            query = query.Where(t => t.Title.ToLower().Contains(title.ToLower())
                || (titleKeyCode != null && titleKeyNumber != null
                    && t.TaskNumber == titleKeyNumber.Value
                    && _db.Projects.Any(p => p.Id == t.ProjectId && EF.Functions.ILike(p.Code, titleKeyCode)))
                || (titleKeyCode == null && titleKeyNumber != null && t.TaskNumber == titleKeyNumber.Value));
        }
        if (excludeDone)         query = query.Where(t => t.Status != Domain.Tasks.TaskStatus.Done);
        // A shared project board stays peer-to-peer: a team lead's or department head's own tasks
        // don't surface to the individual contributors on that project.
        if (excludeAssigneeIds is { Count: > 0 })
            query = query.Where(t => t.AssigneeId == null || !excludeAssigneeIds.Contains(t.AssigneeId.Value));
        // Strict match: discipline is an explicit filter choice (the Task List's own Discipline
        // picker, which anyone — not just the viewing engineer's own default — can point at any
        // value), so it must narrow to exactly that discipline. A prior "null also passes" rule
        // meant to keep generic/undisciplined chores visible on an engineer's own filtered board
        // instead made the filter nearly useless for anyone auditing a specific discipline's work —
        // most tasks have no discipline set at all, so searching e.g. Product silently included
        // every undisciplined task regardless of who it was actually assigned to or what they do.
        if (discipline.HasValue) query = query.Where(t => t.Discipline == discipline.Value);
        var hasProjFilter     = departmentProjectIds  is { Count: > 0 };
        var hasAssigneeFilter = departmentAssigneeIds is { Count: > 0 };
        var hasSprintFilter   = departmentSprintIds   is { Count: > 0 };

        if (hasAssigneeFilter || hasSprintFilter)
            query = query.Where(t =>
                (hasProjFilter     && departmentProjectIds!.Contains(t.ProjectId) && t.AssigneeId == null && t.SprintId == null) ||
                (hasAssigneeFilter && t.AssigneeId != null && departmentAssigneeIds!.Contains(t.AssigneeId.Value)) ||
                (hasSprintFilter   && t.AssigneeId == null && t.SprintId != null && departmentSprintIds!.Contains(t.SprintId.Value)));
        if (status.HasValue)     query = query.Where(t => t.Status == status.Value);
        if (taskType.HasValue)   query = query.Where(t => t.Type == taskType.Value);
        if (sprintId.HasValue)   query = query.Where(t => t.SprintId == sprintId.Value);
        if (noSprint)            query = query.Where(t => t.SprintId == null);
        if (epicId.HasValue)     query = query.Where(t => t.EpicId == epicId.Value);

        // Explicit sort: one flat order by the chosen column (tiebroken by Id) across every task,
        // regardless of status — not layered on top of the Active-first grouping below, since
        // mixing the two would look broken (e.g. sorting by due date but Active tasks still
        // floating to the top out of date order). This is a fully separate code path from the
        // default order below, so the default (no sort chosen) behavior is never touched by it.
        if (sortBy is not null)
        {
            var descending = string.Equals(sortDirection, "desc", StringComparison.OrdinalIgnoreCase);

            string? cursorValue = null;
            Guid cursorRowId = Guid.Empty;
            var hasCursor = cursor is not null && TaskListCursor.TryDecodeSorted(cursor, out _, out _, out cursorValue, out cursorRowId);

            switch (sortBy.ToLowerInvariant())
            {
                case "title":
                    if (hasCursor)
                    {
                        var v = cursorValue ?? "";
                        query = descending
                            ? query.Where(t => string.Compare(t.Title, v) < 0 || (t.Title == v && t.Id > cursorRowId))
                            : query.Where(t => string.Compare(t.Title, v) > 0 || (t.Title == v && t.Id > cursorRowId));
                    }
                    query = descending
                        ? query.OrderByDescending(t => t.Title).ThenBy(t => t.Id)
                        : query.OrderBy(t => t.Title).ThenBy(t => t.Id);
                    break;

                case "points":
                    if (hasCursor && int.TryParse(cursorValue, out var pv))
                    {
                        query = descending
                            ? query.Where(t => t.Points < pv || (t.Points == pv && t.Id > cursorRowId))
                            : query.Where(t => t.Points > pv || (t.Points == pv && t.Id > cursorRowId));
                    }
                    query = descending
                        ? query.OrderByDescending(t => t.Points).ThenBy(t => t.Id)
                        : query.OrderBy(t => t.Points).ThenBy(t => t.Id);
                    break;

                case "status":
                    if (hasCursor && Enum.TryParse<Domain.Tasks.TaskStatus>(cursorValue, out var sv))
                    {
                        query = descending
                            ? query.Where(t => t.Status < sv || (t.Status == sv && t.Id > cursorRowId))
                            : query.Where(t => t.Status > sv || (t.Status == sv && t.Id > cursorRowId));
                    }
                    query = descending
                        ? query.OrderByDescending(t => t.Status).ThenBy(t => t.Id)
                        : query.OrderBy(t => t.Status).ThenBy(t => t.Id);
                    break;

                case "duedate":
                {
                    var cv = hasCursor && cursorValue is not null && DateOnly.TryParse(cursorValue, out var dv) ? dv : (DateOnly?)null;
                    var cursorWasNull = hasCursor && cursorValue is null;
                    if (hasCursor)
                    {
                        // Nulls always sort last, regardless of direction.
                        query = cursorWasNull
                            ? query.Where(t => t.DueDate == null && t.Id > cursorRowId)
                            : query.Where(t => descending
                                ? (t.DueDate != null && (t.DueDate < cv || (t.DueDate == cv && t.Id > cursorRowId)))
                                : ((t.DueDate != null && (t.DueDate > cv || (t.DueDate == cv && t.Id > cursorRowId))) || t.DueDate == null));
                    }
                    query = descending
                        ? query.OrderByDescending(t => t.DueDate == null ? 0 : 1).ThenByDescending(t => t.DueDate).ThenBy(t => t.Id)
                        : query.OrderBy(t => t.DueDate == null ? 1 : 0).ThenBy(t => t.DueDate).ThenBy(t => t.Id);
                    break;
                }

                case "actualenddate":
                {
                    var cv = hasCursor && cursorValue is not null && DateOnly.TryParse(cursorValue, out var dv) ? dv : (DateOnly?)null;
                    var cursorWasNull = hasCursor && cursorValue is null;
                    if (hasCursor)
                    {
                        query = cursorWasNull
                            ? query.Where(t => t.ActualEndDate == null && t.Id > cursorRowId)
                            : query.Where(t => descending
                                ? (t.ActualEndDate != null && (t.ActualEndDate < cv || (t.ActualEndDate == cv && t.Id > cursorRowId)))
                                : ((t.ActualEndDate != null && (t.ActualEndDate > cv || (t.ActualEndDate == cv && t.Id > cursorRowId))) || t.ActualEndDate == null));
                    }
                    query = descending
                        ? query.OrderByDescending(t => t.ActualEndDate == null ? 0 : 1).ThenByDescending(t => t.ActualEndDate).ThenBy(t => t.Id)
                        : query.OrderBy(t => t.ActualEndDate == null ? 1 : 0).ThenBy(t => t.ActualEndDate).ThenBy(t => t.Id);
                    break;
                }
            }

            var sortedItems = await query.Take(limit + 1).ToListAsync(ct);

            string? nextSortedCursor = null;
            if (sortedItems.Count > limit)
            {
                sortedItems.RemoveAt(sortedItems.Count - 1);
                var last = sortedItems[^1];
                string? lastValue = sortBy.ToLowerInvariant() switch
                {
                    "title" => last.Title,
                    "points" => last.Points.ToString(),
                    "status" => last.Status.ToString(),
                    "duedate" => last.DueDate?.ToString("O", CultureInfo.InvariantCulture),
                    "actualenddate" => last.ActualEndDate?.ToString("O", CultureInfo.InvariantCulture),
                    _ => null,
                };
                nextSortedCursor = TaskListCursor.EncodeSorted(sortBy, descending ? "desc" : "asc", lastValue, last.Id);
            }

            return (sortedItems, nextSortedCursor);
        }

        var active = Domain.Tasks.TaskStatus.Active;

        if (cursor is not null && TaskListCursor.TryDecode(cursor, out var cursorIsActive, out var cursorActivatedAt, out var cursorDate, out var cursorId))
        {
            query = cursorIsActive
                // After an Active-group row: remaining Active tasks with an earlier ActivatedAt
                // (the group sorts descending), tiebroken by Id — OR any non-Active task, since the
                // whole non-Active group sorts after Active regardless of its own fields.
                ? query.Where(t =>
                    (t.Status == active && (t.ActivatedAt < cursorActivatedAt || (t.ActivatedAt == cursorActivatedAt && t.Id > cursorId)))
                    || t.Status != active)
                // After a non-Active-group row: remaining non-Active tasks past this due-date/id
                // position — same keyset shape as before this change, just scoped to the group.
                : query.Where(t => t.Status != active && (
                    cursorDate.HasValue
                        ? (t.DueDate > cursorDate || (t.DueDate == cursorDate && t.Id > cursorId) || t.DueDate == null)
                        : (t.DueDate == null && t.Id > cursorId)));
        }

        // Active tasks first (most recently activated first), then everything else in the
        // pre-existing due-date order (undated/backlog tasks last).
        var items = await query
            .OrderBy(t => t.Status == active ? 0 : 1)
            .ThenByDescending(t => t.Status == active ? t.ActivatedAt : (DateTime?)null)
            .ThenBy(t => t.DueDate == null ? 1 : 0)
            .ThenBy(t => t.DueDate)
            .ThenBy(t => t.Id)
            .Take(limit + 1)
            .ToListAsync(ct);

        string? nextCursor = null;
        if (items.Count > limit)
        {
            items.RemoveAt(items.Count - 1);
            var last = items[^1];
            var lastIsActive = last.Status == active;
            nextCursor = TaskListCursor.Encode(lastIsActive, lastIsActive ? last.ActivatedAt : null, lastIsActive ? null : last.DueDate, last.Id);
        }

        return (items, nextCursor);
    }

    public async Task<IReadOnlyList<PulseTask>> GetActiveByAssigneeAsync(Guid assigneeId, CancellationToken ct = default) =>
        await _db.Tasks
            .Where(t => t.AssigneeId == assigneeId && t.Status != Domain.Tasks.TaskStatus.Done)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<PulseTask>> GetHandedOffByAsync(Guid backendAssigneeId, CancellationToken ct = default) =>
        await _db.Tasks
            .Where(t => t.BackendAssigneeId == backendAssigneeId && t.AssigneeId != backendAssigneeId)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<PulseTask>> GetAllActiveAsync(CancellationToken ct = default) =>
        await WithoutPersonal(_db.Tasks).Where(t => t.Status != Domain.Tasks.TaskStatus.Done).ToListAsync(ct);

    public async Task<IReadOnlyList<PulseTask>> GetBlockedTasksAsync(CancellationToken ct = default) =>
        await WithoutPersonal(_db.Tasks).Where(t => t.Status == Domain.Tasks.TaskStatus.Blocked).ToListAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, DateTime>> GetBlockedSinceAsync(IReadOnlyList<Guid> taskIds, CancellationToken ct = default)
    {
        if (taskIds.Count == 0) return new Dictionary<Guid, DateTime>();
        var rows = await _db.TaskHistory
            .Where(h => taskIds.Contains(h.TaskId) && h.Field == "status" && h.NewValue == "Blocked")
            .GroupBy(h => h.TaskId)
            .Select(g => new { TaskId = g.Key, At = g.Max(h => h.ChangedAt) })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.TaskId, r => r.At);
    }

    public async Task<IReadOnlyList<PulseTask>> GetEscalationCandidatesAsync(int daysLookahead = 3, CancellationToken ct = default)
    {
        var threshold = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(daysLookahead));
        return await WithoutPersonal(_db.Tasks)
            .Where(t => t.Status != Domain.Tasks.TaskStatus.Done
                     && t.Status != Domain.Tasks.TaskStatus.Backlog
                     && t.DueDate <= threshold)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<PulseTask>> GetPersonalEscalationCandidatesAsync(int daysLookahead = 3, CancellationToken ct = default)
    {
        var threshold = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(daysLookahead));
        return await _db.Tasks
            .Where(t => _db.Projects.Any(p => p.Id == t.ProjectId && p.PersonalOwnerId != null)
                     && t.Status != Domain.Tasks.TaskStatus.Done
                     && t.Status != Domain.Tasks.TaskStatus.Backlog
                     && t.DueDate <= threshold)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<PulseTask>> GetBySprintAsync(Guid sprintId, CancellationToken ct = default) =>
        await _db.Tasks.Where(t => t.SprintId == sprintId).ToListAsync(ct);

    public async Task<IReadOnlyList<PulseTask>> GetByProjectAsync(Guid projectId, CancellationToken ct = default) =>
        await _db.Tasks.Where(t => t.ProjectId == projectId).ToListAsync(ct);

    public async Task<Dictionary<Guid, IReadOnlyList<PulseTask>>> GetBySprintIdsAsync(IReadOnlyList<Guid> sprintIds, CancellationToken ct = default)
    {
        if (sprintIds.Count == 0) return new Dictionary<Guid, IReadOnlyList<PulseTask>>();

        var tasks = await _db.Tasks.Where(t => t.SprintId != null && sprintIds.Contains(t.SprintId.Value)).ToListAsync(ct);
        return tasks
            .GroupBy(t => t.SprintId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<PulseTask>)g.ToList());
    }

    public async Task<TaskPerformanceStats> GetPerformanceStatsAsync(
        Guid assigneeId, DateTime from, DateTime to, Guid? projectId = null, CancellationToken ct = default)
    {
        // Credited via TaskHistory.CreditedEngineerId (falls back to the task's current AssigneeId
        // for history rows written before that column existed) rather than the task's live
        // AssigneeId directly — same basis as GetDeliveredPointsInRangeByAssigneesAsync, so a task
        // that was on loan or handed off backend->frontend at completion still attributes to the
        // right engineer. Excludes QA sub-tasks: a reviewer's accepted review would otherwise
        // double-count the same points already attributed to the original task's author, breaking
        // reconciliation between an individual's "delivered points" and the team-wide total.
        var doneQuery = _db.TaskHistory
            .Where(h => h.Field == "status" && h.NewValue == "Done")
            .Join(_db.Tasks, h => h.TaskId, t => t.Id, (h, t) => new { h, t })
            .Where(x => (x.h.CreditedEngineerId ?? x.t.AssigneeId) == assigneeId && x.t.ParentTaskId == null);
        if (projectId.HasValue)
            doneQuery = doneQuery.Where(x => x.t.ProjectId == projectId.Value);

        var doneRows = await doneQuery
            .Select(x => new { x.t.Id, x.t.Points, x.t.DueDate, x.t.ActualEndDate, x.t.CreatedAt, x.h.ChangedAt })
            .ToListAsync(ct);

        // A task could in principle have more than one Done-transition in its history; take the
        // latest, mirroring the "max ChangedAt per task" this replaced.
        var completedInRange = doneRows
            .GroupBy(x => x.Id)
            .Select(g => g.OrderByDescending(x => x.ChangedAt).First())
            .Where(x => x.ChangedAt >= from && x.ChangedAt <= to)
            .ToList();

        var deliveredPoints = completedInRange.Sum(t => t.Points);
        var tasksCompleted = completedInRange.Count;
        // A completed task with no due date was never given a deadline to keep, so it's excluded
        // from both sides of the on-time ratio rather than counted as trivially on-time — a
        // completed-tasks-heavy but rarely-due-dated engineer no longer shows a misleadingly high
        // (or even perfect) on-time rate for punctuality that was never actually measured.
        var tasksWithDueDate = completedInRange.Count(t => t.DueDate.HasValue);
        var tasksCompletedOnTime = completedInRange.Count(t =>
            t.DueDate.HasValue && t.ActualEndDate.HasValue && t.ActualEndDate.Value <= t.DueDate.Value);
        double? avgCycleTimeDays = tasksCompleted == 0
            ? null
            : completedInRange.Average(t => PerformanceMetricsCalculator.CycleTimeDays(t.CreatedAt, t.ActualEndDate, t.ChangedAt));

        // QA outcomes: among this engineer's tasks, how many were sent to QA within the range,
        // and how many of those later came back rejected (regardless of when the reject happened).
        var assigneeTaskIdsQuery = _db.Tasks.Where(t => t.AssigneeId == assigneeId);
        if (projectId.HasValue)
            assigneeTaskIdsQuery = assigneeTaskIdsQuery.Where(t => t.ProjectId == projectId.Value);
        var assigneeTaskIds = await assigneeTaskIdsQuery.Select(t => t.Id).ToListAsync(ct);

        var sentToQaIds = assigneeTaskIds.Count == 0
            ? []
            : await _db.TaskHistory
                .Where(h => assigneeTaskIds.Contains(h.TaskId) && h.Field == "status" && h.NewValue == "InQa"
                         && h.ChangedAt >= from && h.ChangedAt <= to)
                .Select(h => h.TaskId)
                .Distinct()
                .ToListAsync(ct);

        var rejectedCount = sentToQaIds.Count == 0
            ? 0
            : await _db.TaskHistory
                .Where(h => sentToQaIds.Contains(h.TaskId) && h.Field == "status" && h.OldValue == "InQa" && h.NewValue == "Active")
                .Select(h => h.TaskId)
                .Distinct()
                .CountAsync(ct);

        return new TaskPerformanceStats(
            deliveredPoints, tasksCompleted, tasksWithDueDate, tasksCompletedOnTime, avgCycleTimeDays, sentToQaIds.Count, rejectedCount);
    }

    public async Task<IReadOnlyList<Guid>> GetProjectIdsBySprintAsync(Guid sprintId, CancellationToken ct = default) =>
        await _db.Tasks.Where(t => t.SprintId == sprintId).Select(t => t.ProjectId).Distinct().ToListAsync(ct);

    public async Task<bool> IsAssignedInSprintAsync(Guid sprintId, Guid engineerId, CancellationToken ct = default) =>
        await _db.Tasks.AnyAsync(t => t.SprintId == sprintId && t.AssigneeId == engineerId, ct);

    public async Task<bool> HasAssignedTaskInProjectAsync(Guid engineerId, Guid projectId, CancellationToken ct = default) =>
        await _db.Tasks.AnyAsync(t => t.ProjectId == projectId && t.AssigneeId == engineerId, ct);

    public async Task<IReadOnlyList<PulseTask>> SearchAsync(string q, int limit, Guid viewerId, CancellationToken ct = default)
    {
        var (code, number) = ParseTaskKey(q);

        var matchingIds = await (
            from t in _db.Tasks
            join p in _db.Projects on t.ProjectId equals p.Id
            where (p.PersonalOwnerId == null || p.PersonalOwnerId == viewerId)
               && (EF.Functions.ILike(t.Title, $"%{q}%")
               || (code != null && number != null && t.TaskNumber == number.Value && EF.Functions.ILike(p.Code, code))
               || (code == null && number != null && t.TaskNumber == number.Value))
            select t.Id
        ).ToListAsync(ct);

        return await _db.Tasks
            .Where(t => matchingIds.Contains(t.Id))
            .OrderBy(t => t.DueDate == null ? 1 : 0)
            .ThenBy(t => t.DueDate)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<(DateOnly Date, int Points)>> GetBurndownDataAsync(Guid sprintId, CancellationToken ct = default)
    {
        var sprintTasks = await _db.Tasks
            .Where(t => t.SprintId == sprintId)
            .ExcludingQaSubtasks()
            .Select(t => new { t.Id, t.Points })
            .ToListAsync(ct);

        if (sprintTasks.Count == 0)
            return [];

        var taskPoints = sprintTasks.ToDictionary(t => t.Id, t => t.Points);
        var taskIds    = taskPoints.Keys.ToList();

        var completions = await _db.TaskHistory
            .Where(h => taskIds.Contains(h.TaskId) && h.Field == "status" && h.NewValue == "Done")
            .Select(h => new { h.TaskId, h.ChangedAt })
            .ToListAsync(ct);

        return completions
            .Select(c => (DateOnly.FromDateTime(c.ChangedAt.ToUniversalTime()),
                          taskPoints.TryGetValue(c.TaskId, out var pts) ? pts : 0))
            .ToList();
    }

    public async Task<Dictionary<Guid, (int Total, int Completed)>> GetTaskProgressByEpicsAsync(IReadOnlyList<Guid> epicIds, CancellationToken ct = default)
    {
        if (epicIds.Count == 0) return [];

        var rows = await _db.Tasks
            .Where(t => t.EpicId != null && epicIds.Contains(t.EpicId!.Value))
            .GroupBy(t => t.EpicId!.Value)
            .Select(g => new { EpicId = g.Key, Total = g.Count(), Completed = g.Count(t => t.Status == Domain.Tasks.TaskStatus.Done) })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.EpicId, r => (r.Total, r.Completed));
    }

    public async Task<(IReadOnlyList<ProjectActivityEntry> Items, string? NextCursor)> GetRecentActivityByProjectAsync(
        Guid projectId, int limit, string? cursor, CancellationToken ct = default)
    {
        var query = from h in _db.TaskHistory
                    join t in _db.Tasks on h.TaskId equals t.Id
                    where t.ProjectId == projectId
                    select new { h.Id, h.TaskId, t.Title, h.Field, h.OldValue, h.NewValue, h.ActorId, h.ChangedAt, h.Context, h.Reason };

        if (cursor is not null && ProjectActivityCursor.TryDecode(cursor, out var cursorChangedAt, out var cursorId))
        {
            query = query.Where(e =>
                e.ChangedAt < cursorChangedAt || (e.ChangedAt == cursorChangedAt && e.Id < cursorId));
        }

        var rows = await query
            .OrderByDescending(e => e.ChangedAt)
            .ThenByDescending(e => e.Id)
            .Take(limit + 1)
            .ToListAsync(ct);

        string? nextCursor = null;
        if (rows.Count > limit)
        {
            rows.RemoveAt(rows.Count - 1);
            nextCursor = ProjectActivityCursor.Encode(rows[^1].ChangedAt, rows[^1].Id);
        }

        var items = rows
            .Select(e => new ProjectActivityEntry(e.Id, e.TaskId, e.Title, e.Field, e.OldValue, e.NewValue, e.ActorId, e.ChangedAt, e.Context, e.Reason))
            .ToList();

        return (items, nextCursor);
    }

    public async Task<IReadOnlyList<WeeklyThroughputPoint>> GetWeeklyThroughputByAssigneeAsync(Guid engineerId, CancellationToken ct = default)
    {
        var sixWeeksAgo = DateTime.UtcNow.AddDays(-42);

        // Credited via CreditedEngineerId (falls back to current AssigneeId) — see
        // GetPerformanceStatsAsync for why. Excludes QA sub-tasks — see GetPerformanceStatsAsync
        // for why.
        var completions = await _db.TaskHistory
            .Join(_db.Tasks, h => h.TaskId, t => t.Id, (h, t) => new { h, t })
            .Where(x => (x.h.CreditedEngineerId ?? x.t.AssigneeId) == engineerId
                     && x.t.ParentTaskId == null
                     && x.h.Field == "status"
                     && x.h.NewValue == "Done"
                     && x.h.ChangedAt >= sixWeeksAgo)
            .Select(x => new { x.t.Points, x.h.ChangedAt })
            .ToListAsync(ct);

        return completions
            .GroupBy(x =>
            {
                var dow = (int)x.ChangedAt.DayOfWeek;
                return DateOnly.FromDateTime(x.ChangedAt.AddDays(dow == 0 ? -6 : 1 - dow));
            })
            .Select(g => new WeeklyThroughputPoint(g.Key, g.Sum(x => x.Points)))
            .OrderBy(p => p.WeekOf)
            .ToList();
    }

    public async Task<IReadOnlyList<WeeklyThroughputPoint>> GetWeeklyThroughputByTeamAsync(Guid teamId, CancellationToken ct = default)
    {
        var teamEngineerIds = await _db.Engineers
            .Where(e => e.TeamId == teamId)
            .Select(e => e.Id)
            .ToListAsync(ct);

        if (teamEngineerIds.Count == 0) return [];

        var sixWeeksAgo = DateTime.UtcNow.AddDays(-42);

        // Team-wide throughput — exclude QA sub-tasks so a task that went through QA isn't
        // counted as two separate pieces of scope across the team.
        var completions = await _db.TaskHistory
            .Join(_db.Tasks, h => h.TaskId, t => t.Id, (h, t) => new { h, t })
            .Where(x => x.t.AssigneeId.HasValue
                     && teamEngineerIds.Contains(x.t.AssigneeId.Value)
                     && x.t.ParentTaskId == null
                     && x.h.Field == "status"
                     && x.h.NewValue == "Done"
                     && x.h.ChangedAt >= sixWeeksAgo)
            .Select(x => new { x.t.Points, x.h.ChangedAt })
            .ToListAsync(ct);

        return completions
            .GroupBy(x =>
            {
                var dow = (int)x.ChangedAt.DayOfWeek;
                return DateOnly.FromDateTime(x.ChangedAt.AddDays(dow == 0 ? -6 : 1 - dow));
            })
            .Select(g => new WeeklyThroughputPoint(g.Key, g.Sum(x => x.Points)))
            .OrderBy(p => p.WeekOf)
            .ToList();
    }

    public async Task<IReadOnlyList<WeeklyThroughputPoint>> GetWeeklyThroughputOrgWideAsync(CancellationToken ct = default)
    {
        var sixWeeksAgo = DateTime.UtcNow.AddDays(-42);

        // Org-wide throughput — exclude QA sub-tasks so a task that went through QA isn't counted
        // as two separate pieces of scope, matching GetWeeklyThroughputByTeamAsync.
        var completions = await _db.TaskHistory
            .Join(_db.Tasks, h => h.TaskId, t => t.Id, (h, t) => new { h, t })
            .Where(x => x.t.AssigneeId.HasValue
                     && x.t.ParentTaskId == null
                     && x.h.Field == "status"
                     && x.h.NewValue == "Done"
                     && x.h.ChangedAt >= sixWeeksAgo)
            .Select(x => new { x.t.Points, x.h.ChangedAt })
            .ToListAsync(ct);

        return completions
            .GroupBy(x =>
            {
                var dow = (int)x.ChangedAt.DayOfWeek;
                return DateOnly.FromDateTime(x.ChangedAt.AddDays(dow == 0 ? -6 : 1 - dow));
            })
            .Select(g => new WeeklyThroughputPoint(g.Key, g.Sum(x => x.Points)))
            .OrderBy(p => p.WeekOf)
            .ToList();
    }

    public async Task<int> GetDeliveredPointsInRangeAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        var taskIds = await _db.TaskHistory
            .Where(h => h.Field == "status" && h.NewValue == "Done" && h.ChangedAt >= from && h.ChangedAt <= to)
            .Select(h => h.TaskId)
            .Distinct()
            .ToListAsync(ct);

        if (taskIds.Count == 0) return 0;

        // Org-wide delivered points — exclude QA sub-tasks so a task that went through QA isn't
        // counted as two separate pieces of scope.
        return await _db.Tasks
            .Where(t => taskIds.Contains(t.Id))
            .ExcludingQaSubtasks()
            .SumAsync(t => t.Points, ct);
    }

    public async Task<int> GetDeliveredPointsInRangeAsync(DateTime from, DateTime to, IReadOnlyList<Guid> projectIds, CancellationToken ct = default)
    {
        var taskIds = await _db.TaskHistory
            .Where(h => h.Field == "status" && h.NewValue == "Done" && h.ChangedAt >= from && h.ChangedAt <= to)
            .Select(h => h.TaskId)
            .Distinct()
            .ToListAsync(ct);

        if (taskIds.Count == 0) return 0;

        return await _db.Tasks
            .Where(t => taskIds.Contains(t.Id) && projectIds.Contains(t.ProjectId))
            .ExcludingQaSubtasks()
            .SumAsync(t => t.Points, ct);
    }

    public async Task<double?> GetAvgCycleTimeDaysInRangeAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        var doneRows = await _db.TaskHistory
            .Where(h => h.Field == "status" && h.NewValue == "Done")
            .Join(_db.Tasks.ExcludingQaSubtasks(), h => h.TaskId, t => t.Id,
                (h, t) => new { t.Id, t.CreatedAt, t.ActualEndDate, h.ChangedAt })
            .ToListAsync(ct);

        return AverageCycleTimeInRange(doneRows.Select(x => (x.Id, x.CreatedAt, x.ActualEndDate, x.ChangedAt)), from, to);
    }

    public async Task<double?> GetAvgCycleTimeDaysInRangeAsync(DateTime from, DateTime to, IReadOnlyList<Guid> projectIds, CancellationToken ct = default)
    {
        var doneRows = await _db.TaskHistory
            .Where(h => h.Field == "status" && h.NewValue == "Done")
            .Join(_db.Tasks.ExcludingQaSubtasks().Where(t => projectIds.Contains(t.ProjectId)), h => h.TaskId, t => t.Id,
                (h, t) => new { t.Id, t.CreatedAt, t.ActualEndDate, h.ChangedAt })
            .ToListAsync(ct);

        return AverageCycleTimeInRange(doneRows.Select(x => (x.Id, x.CreatedAt, x.ActualEndDate, x.ChangedAt)), from, to);
    }

    /// <summary>Shared by both GetAvgCycleTimeDaysInRangeAsync overloads — a task can in principle have more
    /// than one Done-transition in its history (reopened and redone); takes the latest, same as
    /// GetPerformanceStatsAsync's own avgCycleTimeDays, then filters to the requested range by that
    /// transition's timestamp.</summary>
    private static double? AverageCycleTimeInRange(
        IEnumerable<(Guid Id, DateTime CreatedAt, DateOnly? ActualEndDate, DateTime ChangedAt)> doneRows, DateTime from, DateTime to)
    {
        var completedInRange = doneRows
            .GroupBy(x => x.Id)
            .Select(g => g.OrderByDescending(x => x.ChangedAt).First())
            .Where(x => x.ChangedAt >= from && x.ChangedAt <= to)
            .ToList();

        return completedInRange.Count == 0
            ? null
            : completedInRange.Average(t => PerformanceMetricsCalculator.CycleTimeDays(t.CreatedAt, t.ActualEndDate, t.ChangedAt));
    }

    public async Task<double?> GetAvgPrApprovalHoursInRangeAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        var requests = await _db.TaskHistory
            .Where(h => h.Field == "pr_link" && h.NewValue != null)
            .Select(h => new { h.TaskId, h.ChangedAt })
            .ToListAsync(ct);
        var approvals = await _db.TaskHistory
            .Where(h => h.Field == "pr_approved_at" && h.ChangedAt >= from && h.ChangedAt <= to)
            .Select(h => new { h.TaskId, h.ChangedAt })
            .ToListAsync(ct);

        return AveragePrApprovalHours(
            requests.Select(x => (x.TaskId, x.ChangedAt)),
            approvals.Select(x => (x.TaskId, x.ChangedAt)));
    }

    public async Task<double?> GetAvgPrApprovalHoursInRangeAsync(DateTime from, DateTime to, IReadOnlyList<Guid> projectIds, CancellationToken ct = default)
    {
        var taskIdsInProjects = _db.Tasks.Where(t => projectIds.Contains(t.ProjectId)).Select(t => t.Id);
        var requests = await _db.TaskHistory
            .Where(h => h.Field == "pr_link" && h.NewValue != null)
            .Join(taskIdsInProjects, h => h.TaskId, id => id, (h, id) => new { h.TaskId, h.ChangedAt })
            .ToListAsync(ct);
        var approvals = await _db.TaskHistory
            .Where(h => h.Field == "pr_approved_at" && h.ChangedAt >= from && h.ChangedAt <= to)
            .Join(taskIdsInProjects, h => h.TaskId, id => id, (h, id) => new { h.TaskId, h.ChangedAt })
            .ToListAsync(ct);

        return AveragePrApprovalHours(
            requests.Select(x => (x.TaskId, x.ChangedAt)),
            approvals.Select(x => (x.TaskId, x.ChangedAt)));
    }

    /// <summary>Shared by both GetAvgPrApprovalHoursInRangeAsync overloads — pairs each already
    /// range-filtered approval with the most recent request that preceded it, per task.</summary>
    private static double? AveragePrApprovalHours(
        IEnumerable<(Guid TaskId, DateTime ChangedAt)> requests,
        IEnumerable<(Guid TaskId, DateTime ChangedAt)> approvalsInRange)
    {
        var requestsByTask = requests
            .GroupBy(r => r.TaskId)
            .ToDictionary(g => g.Key, g => g.Select(r => r.ChangedAt).OrderBy(x => x).ToList());

        var durationsHours = new List<double>();
        foreach (var approval in approvalsInRange)
        {
            if (!requestsByTask.TryGetValue(approval.TaskId, out var taskRequests)) continue;
            var precedingRequests = taskRequests.Where(r => r <= approval.ChangedAt).ToList();
            if (precedingRequests.Count == 0) continue;
            durationsHours.Add((approval.ChangedAt - precedingRequests.Max()).TotalHours);
        }

        return durationsHours.Count == 0 ? null : durationsHours.Average();
    }

    public async Task<int> GetDeliveredPointsInRangeByAssigneesAsync(DateTime from, DateTime to, IReadOnlyList<Guid> assigneeIds, CancellationToken ct = default)
    {
        // Credit the engineer CreditedEngineerId snapshotted at completion time (the lending
        // engineer if the task was on loan when it finished, otherwise the assignee) rather than
        // the task's current assignee, which may have changed since. Falls back to the task's
        // current AssigneeId for history rows written before this column existed.
        var doneEvents = await _db.TaskHistory
            .Where(h => h.Field == "status" && h.NewValue == "Done" && h.ChangedAt >= from && h.ChangedAt <= to)
            .Join(_db.Tasks, h => h.TaskId, t => t.Id, (h, t) => new { h.TaskId, Credited = h.CreditedEngineerId ?? t.AssigneeId, h.ChangedAt })
            .ToListAsync(ct);

        var creditedTaskIds = doneEvents
            .GroupBy(x => x.TaskId)
            .Select(g => g.OrderByDescending(x => x.ChangedAt).First())
            .Where(x => x.Credited.HasValue && assigneeIds.Contains(x.Credited.Value))
            .Select(x => x.TaskId)
            .ToList();

        if (creditedTaskIds.Count == 0) return 0;

        // Team-wide delivered points (assigneeIds is a whole team's roster) — same exclusion as
        // the org-wide overloads above.
        return await _db.Tasks
            .Where(t => creditedTaskIds.Contains(t.Id))
            .ExcludingQaSubtasks()
            .SumAsync(t => t.Points, ct);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetCompletedTaskCountByEngineerInRangeAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        // Same CreditedEngineerId-with-fallback basis as GetDeliveredPointsInRangeByAssigneesAsync.
        var doneEvents = await _db.TaskHistory
            .Where(h => h.Field == "status" && h.NewValue == "Done" && h.ChangedAt >= from && h.ChangedAt <= to)
            .Join(_db.Tasks, h => h.TaskId, t => t.Id, (h, t) => new { h.TaskId, Credited = h.CreditedEngineerId ?? t.AssigneeId, h.ChangedAt, t.ParentTaskId })
            .ToListAsync(ct);

        var latestPerTask = doneEvents
            .Where(x => x.ParentTaskId == null)
            .GroupBy(x => x.TaskId)
            .Select(g => g.OrderByDescending(x => x.ChangedAt).First())
            .Where(x => x.Credited.HasValue)
            .ToList();

        return latestPerTask
            .GroupBy(x => x.Credited!.Value)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetSubtaskCompletionCountByEngineerInRangeAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        var rows = await _db.TaskHistory
            .Where(h => h.Field == "subtask_completed" && h.ChangedAt >= from && h.ChangedAt <= to)
            .GroupBy(h => h.ActorId)
            .Select(g => new { EngineerId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.EngineerId, r => r.Count);
    }

    public async Task<ProjectTaskCounts> GetProjectTaskCountsAsync(Guid projectId, CancellationToken ct = default)
    {
        var rows = await _db.Tasks
            .Where(t => t.ProjectId == projectId)
            .Select(t => new { t.Status, t.Title, t.SprintId })
            .ToListAsync(ct);

        var sprintIds = rows
            .Where(t => t.SprintId.HasValue)
            .Select(t => t.SprintId!.Value)
            .Distinct()
            .ToList();

        Guid? activeSprintId = null;
        if (sprintIds.Count > 0)
        {
            activeSprintId = await _db.Sprints
                .Where(s => sprintIds.Contains(s.Id) && s.Status == SprintStatus.Active)
                .Select(s => (Guid?)s.Id)
                .FirstOrDefaultAsync(ct);
        }

        var activeCount    = rows.Count(t => t.Status == Domain.Tasks.TaskStatus.Active);
        var blockedCount   = rows.Count(t => t.Status == Domain.Tasks.TaskStatus.Blocked);
        var doneCount      = rows.Count(t => t.Status == Domain.Tasks.TaskStatus.Done);
        var doneThisSprint = activeSprintId.HasValue
            ? rows.Count(t => t.Status == Domain.Tasks.TaskStatus.Done && t.SprintId == activeSprintId)
            : 0;
        var lastBlocker    = rows.FirstOrDefault(t => t.Status == Domain.Tasks.TaskStatus.Blocked)?.Title;

        return new ProjectTaskCounts(activeCount, blockedCount, doneThisSprint, rows.Count, doneCount, lastBlocker, activeSprintId);
    }

    public async Task<ProjectTaskCounts> GetProjectTaskCountsAsync(Guid projectId, IReadOnlyList<Guid> assigneeIds, CancellationToken ct = default)
    {
        var rows = await _db.Tasks
            .Where(t => t.ProjectId == projectId)
            .Select(t => new { t.Status, t.Title, t.SprintId, t.AssigneeId })
            .ToListAsync(ct);

        // Active sprint is a project-wide fact, resolved from every task in the project — not just
        // the given engineers' slice — same as the unscoped overload.
        var sprintIds = rows
            .Where(t => t.SprintId.HasValue)
            .Select(t => t.SprintId!.Value)
            .Distinct()
            .ToList();

        Guid? activeSprintId = null;
        if (sprintIds.Count > 0)
        {
            activeSprintId = await _db.Sprints
                .Where(s => sprintIds.Contains(s.Id) && s.Status == SprintStatus.Active)
                .Select(s => (Guid?)s.Id)
                .FirstOrDefaultAsync(ct);
        }

        var teamRows = rows.Where(t => t.AssigneeId.HasValue && assigneeIds.Contains(t.AssigneeId.Value)).ToList();

        var activeCount    = teamRows.Count(t => t.Status == Domain.Tasks.TaskStatus.Active);
        var blockedCount   = teamRows.Count(t => t.Status == Domain.Tasks.TaskStatus.Blocked);
        var doneCount      = teamRows.Count(t => t.Status == Domain.Tasks.TaskStatus.Done);
        var doneThisSprint = activeSprintId.HasValue
            ? teamRows.Count(t => t.Status == Domain.Tasks.TaskStatus.Done && t.SprintId == activeSprintId)
            : 0;
        var lastBlocker    = teamRows.FirstOrDefault(t => t.Status == Domain.Tasks.TaskStatus.Blocked)?.Title;

        return new ProjectTaskCounts(activeCount, blockedCount, doneThisSprint, teamRows.Count, doneCount, lastBlocker, activeSprintId);
    }

    public async Task<Dictionary<Guid, ProjectTaskCounts>> GetProjectTaskCountsBatchAsync(IReadOnlyList<Guid> projectIds, CancellationToken ct = default)
    {
        if (projectIds.Count == 0) return new Dictionary<Guid, ProjectTaskCounts>();

        var rows = await _db.Tasks
            .Where(t => projectIds.Contains(t.ProjectId))
            .Select(t => new { t.ProjectId, t.Status, t.Title, t.SprintId, t.Priority })
            .ToListAsync(ct);

        var allSprintIds = rows
            .Where(t => t.SprintId.HasValue)
            .Select(t => t.SprintId!.Value)
            .Distinct()
            .ToList();

        var activeSprintIds = allSprintIds.Count == 0
            ? new HashSet<Guid>()
            : (await _db.Sprints
                .Where(s => allSprintIds.Contains(s.Id) && s.Status == SprintStatus.Active)
                .Select(s => s.Id)
                .ToListAsync(ct)).ToHashSet();

        var result = new Dictionary<Guid, ProjectTaskCounts>();
        foreach (var group in rows.GroupBy(t => t.ProjectId))
        {
            var projectRows = group.ToList();
            var activeSprintId = projectRows
                .Where(t => t.SprintId.HasValue && activeSprintIds.Contains(t.SprintId.Value))
                .Select(t => (Guid?)t.SprintId!.Value)
                .FirstOrDefault();

            var activeCount    = projectRows.Count(t => t.Status == Domain.Tasks.TaskStatus.Active);
            var blockedCount   = projectRows.Count(t => t.Status == Domain.Tasks.TaskStatus.Blocked);
            var doneCount      = projectRows.Count(t => t.Status == Domain.Tasks.TaskStatus.Done);
            var doneThisSprint = activeSprintId.HasValue
                ? projectRows.Count(t => t.Status == Domain.Tasks.TaskStatus.Done && t.SprintId == activeSprintId)
                : 0;
            var lastBlocker    = projectRows.FirstOrDefault(t => t.Status == Domain.Tasks.TaskStatus.Blocked)?.Title;
            var highPriorityOpenCount = projectRows.Count(t =>
                t.Priority is >= 4 && t.Status != Domain.Tasks.TaskStatus.Done);

            result[group.Key] = new ProjectTaskCounts(activeCount, blockedCount, doneThisSprint, projectRows.Count, doneCount, lastBlocker, activeSprintId, highPriorityOpenCount);
        }

        // A project with zero tasks still needs an entry so callers can index freely.
        foreach (var projectId in projectIds)
            result.TryAdd(projectId, new ProjectTaskCounts(0, 0, 0, 0, 0, null, null));

        return result;
    }

    public async Task<int> GetNextTaskNumberAsync(Guid projectId, CancellationToken ct = default)
    {
        var max = await _db.Tasks.Where(t => t.ProjectId == projectId)
            .Select(t => (int?)t.TaskNumber).MaxAsync(ct);
        return (max ?? 0) + 1;
    }

    public async Task AddAsync(PulseTask task, CancellationToken ct = default) =>
        await _db.Tasks.AddAsync(task, ct);

    public Task DeleteAsync(PulseTask task, CancellationToken ct = default)
    {
        _db.Tasks.Remove(task);
        return Task.CompletedTask;
    }

    public async Task SaveChangesAsync(CancellationToken ct = default) =>
        await _db.SaveChangesAsync(ct);
}
