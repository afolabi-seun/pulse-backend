using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using MediatR;

namespace Pulse.Application.Tasks.Queries;

public record ListTasksQuery(
    Guid ActorId,
    string ActorRole,
    Guid? ProjectId,
    Guid? AssigneeId,
    string? Status,
    string? TaskType,
    Guid? SprintId,
    Guid? EpicId,
    int Limit,
    string? Cursor,
    bool NoSprint = false,
    string? Title = null,
    Discipline? Discipline = null,
    bool ExcludeDone = false,
    bool NoAssignee = false,
    string? SortBy = null,
    string? SortDirection = null) : IRequest<ServiceResult<PagedResult<TaskDto>>>;

public class ListTasksHandler : IRequestHandler<ListTasksQuery, ServiceResult<PagedResult<TaskDto>>>
{
    private readonly ITaskRepository      _tasks;
    private readonly IEngineerRepository  _engineers;
    private readonly IProjectRepository   _projects;
    private readonly ITeamRepository      _teams;
    private readonly ISprintRepository    _sprints;
    private readonly ISubtaskRepository   _subtasks;
    private readonly IProjectAccessPolicy _access;

    public ListTasksHandler(ITaskRepository tasks, IEngineerRepository engineers,
        IProjectRepository projects, ITeamRepository teams, ISprintRepository sprints,
        ISubtaskRepository subtasks, IProjectAccessPolicy access)
    {
        _tasks     = tasks;
        _engineers = engineers;
        _projects  = projects;
        _teams     = teams;
        _sprints   = sprints;
        _subtasks  = subtasks;
        _access    = access;
    }

    public async Task<ServiceResult<PagedResult<TaskDto>>> Handle(ListTasksQuery query, CancellationToken ct)
    {
        Domain.Tasks.TaskStatus? status = null;
        if (query.Status is not null)
        {
            if (!Enum.TryParse<Domain.Tasks.TaskStatus>(query.Status, ignoreCase: true, out var parsed))
                return ServiceResult<PagedResult<TaskDto>>.Fail("VALIDATION_ERROR", $"Invalid status '{query.Status}'.");
            status = parsed;
        }

        Domain.Tasks.TaskType? taskType = null;
        if (query.TaskType is not null)
        {
            if (!Enum.TryParse<Domain.Tasks.TaskType>(query.TaskType, ignoreCase: true, out var parsedType))
                return ServiceResult<PagedResult<TaskDto>>.Fail("VALIDATION_ERROR", $"Invalid task type '{query.TaskType}'.");
            taskType = parsedType;
        }

        if (query.SortBy is not null && !TaskSortFields.Allowed.Contains(query.SortBy))
            return ServiceResult<PagedResult<TaskDto>>.Fail("VALIDATION_ERROR", $"Cannot sort by '{query.SortBy}'.");
        if (query.SortDirection is not null && !TaskSortFields.Directions.Contains(query.SortDirection))
            return ServiceResult<PagedResult<TaskDto>>.Fail("VALIDATION_ERROR", $"Invalid sort direction '{query.SortDirection}'.");

        var isSorted = query.SortBy is not null;
        if (query.Cursor is not null)
        {
            var validCursor = isSorted
                ? TaskListCursor.TryDecodeSorted(query.Cursor, out _, out _, out _, out _)
                : TaskListCursor.TryDecode(query.Cursor, out _, out _, out _, out _);
            if (!validCursor)
                return ServiceResult<PagedResult<TaskDto>>.Fail("VALIDATION_ERROR", "Invalid pagination cursor.");
        }

        var limit = Math.Clamp(query.Limit, 1, 100);

        // Viewing one specific sprint (e.g. a Sprint Detail board) is all-or-nothing: once the
        // caller is confirmed to have access to that sprint, they should see everything in it. The
        // per-role narrowing below exists for browsing broadly *across* many sprints/projects (an
        // IC's own tasks only, a lead/head's own team/department roster) — applied unconditionally,
        // it wrongly re-narrows a single sprint the caller already has legitimate access to, hiding
        // tasks assigned to anyone outside their own roster (e.g. a QA reviewer from another team)
        // even though the sprint itself, and every task in it, is meant to be fully visible to them.
        if (query.SprintId.HasValue && !query.NoAssignee)
        {
            var sprint = await _sprints.GetByIdAsync(query.SprintId.Value, ct);
            if (sprint is not null &&
                await _access.CanAccessSprintAsync(sprint.Id, sprint.TeamId, query.ActorId, query.ActorRole, ct))
            {
                var (sprintItems, sprintCursor) = await _tasks.ListAsync(
                    query.ProjectId, query.AssigneeId, status, taskType, query.SprintId, query.EpicId, limit, query.Cursor,
                    query.NoSprint, query.Title, discipline: query.Discipline, excludeDone: query.ExcludeDone,
                    sortBy: query.SortBy, sortDirection: query.SortDirection, ct: ct);
                var sprintDtos = await ToDtosAsync(sprintItems, query.ActorId, ct);
                return ServiceResult<PagedResult<TaskDto>>.Ok(
                    new PagedResult<TaskDto>(sprintDtos, sprintCursor, sprintCursor is not null));
            }
            // Sprint not found, or the caller can't access it — fall through to the per-role
            // narrowing below, which degrades to an empty/narrower result rather than erroring.
        }

        // Engineers and designers: see all tasks on the project if they can access it (member or
        // owning team) — a shared board stays peer-to-peer — otherwise only their own tasks. Either
        // way, a team lead's or department head's own tasks never surface: visibility doesn't reach
        // upward past the viewer's own level.
        if (query.ActorRole == Roles.Engineer || query.ActorRole == Roles.Designer)
        {
            var isProjectMember = query.ProjectId.HasValue &&
                await _access.CanAccessProjectAsync(query.ProjectId.Value, query.ActorId, query.ActorRole, ct);

            if (query.NoAssignee)
            {
                // Browsing unclaimed work requires knowing which project's backlog to show —
                // unlike "my tasks", there's no implicit personal scope to fall back to.
                if (!query.ProjectId.HasValue)
                    return ServiceResult<PagedResult<TaskDto>>.Fail("VALIDATION_ERROR", "ProjectId is required when browsing unclaimed tasks.");

                if (!isProjectMember)
                    return ServiceResult<PagedResult<TaskDto>>.Fail("FORBIDDEN", "You do not have access to this project.");

                var (unclaimedItems, unclaimedCursor) = await _tasks.ListAsync(
                    query.ProjectId, null, status, taskType, query.SprintId, query.EpicId, limit, query.Cursor,
                    query.NoSprint, query.Title, discipline: query.Discipline, excludeDone: query.ExcludeDone,
                    noAssignee: true, sortBy: query.SortBy, sortDirection: query.SortDirection, ct: ct);
                var unclaimedDtos = await ToDtosAsync(unclaimedItems, query.ActorId, ct);
                return ServiceResult<PagedResult<TaskDto>>.Ok(
                    new PagedResult<TaskDto>(unclaimedDtos, unclaimedCursor, unclaimedCursor is not null));
            }

            Guid? assigneeFilter = isProjectMember ? null : query.ActorId;

            IReadOnlyList<Guid>? seniorAssigneeIds = null;
            if (isProjectMember)
            {
                var allEngineers = await _engineers.ListActiveAsync(ct);
                seniorAssigneeIds = allEngineers
                    .Where(e => e.Role == Roles.TeamLead || Roles.HeadRoles.Contains(e.Role))
                    .Select(e => e.Id)
                    .ToList();
            }

            var (items, nextCursor) = await _tasks.ListAsync(
                query.ProjectId, assigneeFilter, status, taskType, query.SprintId, query.EpicId, limit, query.Cursor, query.NoSprint, query.Title, discipline: query.Discipline, excludeDone: query.ExcludeDone, excludeAssigneeIds: seniorAssigneeIds,
                sortBy: query.SortBy, sortDirection: query.SortDirection, ct: ct);
            var icDtos = await ToDtosAsync(items, query.ActorId, ct);
            return ServiceResult<PagedResult<TaskDto>>.Ok(
                new PagedResult<TaskDto>(icDtos, nextCursor, nextCursor is not null));
        }

        // Team leads: scoped to their own team's roster (themselves plus their engineers), across
        // every project that team touches. Department heads (non-PMO/Product): the same idea one
        // level up — every team in their department, so they also see their team leads' tasks.
        // PMO/Product heads, PM, and ProductManager fall through unfiltered (see everything).
        IReadOnlyList<Guid>? deptFilter = null;
        IReadOnlyList<Guid>? deptSprintIds = null;
        IReadOnlyList<Guid>? deptProjectIds = null;

        if (query.ActorRole == Roles.TeamLead)
        {
            // Team.TeamLeadId, not the lead's own Engineer.TeamId, is the source of truth for which
            // team a lead leads — creating/updating a team never assigns its lead as one of its own
            // members (mirrors ProjectAccessPolicy.GetLedTeamAsync).
            var ledTeam = await DepartmentScope.GetLedTeamAsync(query.ActorId, _teams, ct);

            if (ledTeam is not null)
            {
                var actorTeamId = ledTeam.Id;
                var allEngineers = await _engineers.ListActiveAsync(ct);

                deptFilter = allEngineers
                    .Where(e => e.TeamId == actorTeamId)
                    .Select(e => e.Id)
                    .ToList();

                var allSprints = await _sprints.ListByTeamAsync(null, ct);
                deptSprintIds = allSprints
                    .Where(s => s.TeamId == actorTeamId)
                    .Select(s => s.Id)
                    .ToList();

                var allProjects = await _projects.ListActiveAsync(ct);
                deptProjectIds = allProjects
                    .Where(p => p.OwnerTeamId == actorTeamId)
                    .Select(p => p.Id)
                    .ToList();
            }
            // Not the designated lead of any team → no filter (see all tasks) — mirrors head behavior.
        }
        else if (Roles.HeadRoles.Contains(query.ActorRole) && query.ActorRole is not (Roles.HeadOfPmo or Roles.HeadOfProduct))
        {
            var allEngineers = await _engineers.ListActiveAsync(ct);
            var actor = allEngineers.FirstOrDefault(e => e.Id == query.ActorId);

            if (actor?.TeamId is Guid actorTeamId)
            {
                var actorTeam = await _teams.GetByIdAsync(actorTeamId, ct);

                if (actorTeam?.Department is string deptName)
                {
                    var allTeams = await _teams.ListAllAsync(ct);
                    var deptTeamIds = allTeams
                        .Where(t => t.Department == deptName)
                        .Select(t => t.Id)
                        .ToHashSet();

                    deptFilter = allEngineers
                        .Where(e => e.TeamId.HasValue && deptTeamIds.Contains(e.TeamId.Value))
                        .Select(e => e.Id)
                        .ToList();

                    var allSprints = await _sprints.ListByTeamAsync(null, ct);
                    deptSprintIds = allSprints
                        .Where(s => deptTeamIds.Contains(s.TeamId))
                        .Select(s => s.Id)
                        .ToList();

                    var allProjects = await _projects.ListActiveAsync(ct);
                    deptProjectIds = allProjects
                        .Where(p => p.OwnerTeamId.HasValue && deptTeamIds.Contains(p.OwnerTeamId.Value))
                        .Select(p => p.Id)
                        .ToList();
                }
                // Head has a team but that team has no dept set → no dept filter (see all tasks)
            }
            // Head has no team assigned → no dept filter (see all tasks)
        }

        var (taskItems, cursor) = await _tasks.ListAsync(
            query.ProjectId, query.AssigneeId, status, taskType, query.SprintId, query.EpicId, limit, query.Cursor, query.NoSprint, query.Title, deptFilter, deptSprintIds, deptProjectIds, query.Discipline, query.ExcludeDone, noAssignee: query.NoAssignee,
            sortBy: query.SortBy, sortDirection: query.SortDirection, ct: ct);

        var dtos = await ToDtosAsync(taskItems, query.ActorId, ct);
        return ServiceResult<PagedResult<TaskDto>>.Ok(new PagedResult<TaskDto>(dtos, cursor, cursor is not null));
    }

    /// <summary>An org-wide listing already leaves personal tasks out; asking for one person's tasks, or one
    /// project, is how their owner reaches them — but it must not be how anyone else does, whatever their
    /// role. Drops tasks in a personal-tasks project (see Project.PersonalOwnerId) that isn't the caller's own.</summary>
    private async Task<(IReadOnlyList<Domain.Tasks.PulseTask> Items, HashSet<Guid> PersonalProjectIds)> WithoutOthersPersonalAsync(
        IReadOnlyList<Domain.Tasks.PulseTask> items, Guid actorId, CancellationToken ct)
    {
        if (items.Count == 0) return (items, []);
        var personal = await _projects.GetPersonalProjectIdsAsync(items.Select(t => t.ProjectId).Distinct().ToList(), ct);
        if (personal.Count == 0) return (items, []);

        var own = await _projects.GetPersonalProjectAsync(actorId, ct);
        var kept = items.Where(t => !personal.Contains(t.ProjectId) || t.ProjectId == own?.Id).ToList();
        return (kept, personal.ToHashSet());
    }

    /// <summary>Denormalizes project name, assignee name, and creator name onto each row via batch lookups,
    /// rather than making the frontend reconcile a task list against separately-scoped project/engineer
    /// lists that may not cover every project or assignee referenced (e.g. a department head's own
    /// engineer list is scoped to their department, but a shared project can have assignees outside it).</summary>
    private async Task<List<TaskDto>> ToDtosAsync(IReadOnlyList<Domain.Tasks.PulseTask> items, Guid actorId, CancellationToken ct)
    {
        var personalIds = new HashSet<Guid>();
        (items, personalIds) = await WithoutOthersPersonalAsync(items, actorId, ct);
        var projectIds = items.Select(t => t.ProjectId).Distinct().ToList();
        var projectNames = projectIds.Count > 0
            ? await _projects.GetNamesByIdsAsync(projectIds, ct)
            : new Dictionary<Guid, string>();
        var projectCodes = projectIds.Count > 0
            ? await _projects.GetCodesByIdsAsync(projectIds, ct)
            : new Dictionary<Guid, string>();

        var assigneeIds = items.Where(t => t.AssigneeId.HasValue).Select(t => t.AssigneeId!.Value)
            .Concat(items.Where(t => t.CreatedById.HasValue).Select(t => t.CreatedById!.Value))
            .Distinct().ToList();
        var engineerNames = assigneeIds.Count > 0
            ? (await _engineers.GetByIdsAsync(assigneeIds, ct)).ToDictionary(e => e.Id, e => e.Name)
            : new Dictionary<Guid, string>();

        var subtaskCounts = await _subtasks.CountsByTaskIdsAsync(items.Select(t => t.Id), ct);

        return items.Select(t =>
        {
            var hasSubtasks = subtaskCounts.TryGetValue(t.Id, out var counts);
            return TaskDto.From(
                t,
                projectNames.TryGetValue(t.ProjectId, out var pName) ? pName : null,
                t.AssigneeId.HasValue && engineerNames.TryGetValue(t.AssigneeId.Value, out var aName) ? aName : null,
                t.CreatedById.HasValue && engineerNames.TryGetValue(t.CreatedById.Value, out var cName) ? cName : null,
                subtasksDone: hasSubtasks ? counts.Done : null,
                subtasksTotal: hasSubtasks ? counts.Total : null,
                projectCode: projectCodes.TryGetValue(t.ProjectId, out var pCode) ? pCode : null) with { IsPersonal = personalIds.Contains(t.ProjectId) };
        }).ToList();
    }
}
