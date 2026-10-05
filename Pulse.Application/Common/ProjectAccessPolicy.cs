using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using Pulse.Domain.Teams;

namespace Pulse.Application.Common;

/// <summary>
/// Object-level authorization for project-scoped resources (tasks, wiki, sprints, epics, …).
/// Single-object endpoints fetch by id and must verify the caller is allowed to see that
/// specific project — role attributes alone only gate <em>which kind</em> of user may call,
/// not <em>which</em> project they may touch. This mirrors the visibility rules already
/// applied by <c>ListTasksQuery</c>.
/// </summary>
public interface IProjectAccessPolicy
{
    /// <summary>Returns true if the actor may access resources belonging to the given project.</summary>
    Task<bool> CanAccessProjectAsync(Guid projectId, Guid actorId, string actorRole, CancellationToken ct = default);

    /// <summary>Returns every engineer who can access this project — see the implementation's
    /// doc comment for exactly which rules this assembles.</summary>
    Task<IReadOnlyList<Guid>> GetAccessibleEngineerIdsAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>Returns true if the actor may access resources owned by the given team.</summary>
    Task<bool> CanAccessTeamAsync(Guid teamId, Guid actorId, string actorRole, CancellationToken ct = default);

    /// <summary>
    /// Returns true if the actor may view a sprint. A sprint is visible to the team that runs it
    /// (and that team's department / global roles), <em>or</em> to anyone who can access a project
    /// that has a task in the sprint — engineers can be members of projects run by other teams.
    /// </summary>
    Task<bool> CanAccessSprintAsync(Guid sprintId, Guid teamId, Guid actorId, string actorRole, CancellationToken ct = default);

    /// <summary>
    /// Returns true if the actor may access a task (and its child data: comments, estimation, …):
    /// they are the assignee, or can access the task's project. False if the task does not exist.
    /// </summary>
    Task<bool> CanAccessTaskAsync(Guid taskId, Guid actorId, string actorRole, CancellationToken ct = default);

    /// <summary>
    /// Returns true if the actor may VIEW a task — everything <see cref="CanAccessTaskAsync"/>
    /// allows, plus a read-only manager's-eye carve-out: a TeamLead/department head can always view
    /// (never edit, comment, assign, start a timer on, or vote on) a task assigned to one of their
    /// own people, even in a project they otherwise have no access to. This mirrors the
    /// assignee-based scoping <c>ListTasksHandler</c> already applies when building their task list,
    /// so a task that shows up there doesn't 403 the moment it's opened. Intended only for the
    /// read-only queries that populate the task detail page — every write-side command keeps using
    /// <see cref="CanAccessTaskAsync"/> unchanged.
    /// </summary>
    Task<bool> CanViewTaskAsync(Guid taskId, Guid actorId, string actorRole, CancellationToken ct = default);
}

public class ProjectAccessPolicy : IProjectAccessPolicy
{
    private readonly IProjectRepository _projects;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;
    private readonly ITaskRepository _tasks;
    private readonly IProjectFollowRepository _follows;

    // Managers, the PMO head, and the Head of Product have organisation-wide visibility.
    // Internal (not private) so ListProjectsHandler can classify roles the same way when computing
    // a per-project CanAccess flag in bulk, without re-deriving or duplicating this list.
    internal static readonly HashSet<string> GlobalRoles =
    [
        Roles.ProductManager, Roles.ProjectManager, Roles.HeadOfPmo, Roles.HeadOfProduct,
    ];

    // Department heads see resources owned by a team in their own department.
    internal static readonly HashSet<string> DepartmentHeadRoles =
    [
        Roles.HeadOfRnD, Roles.HeadOfDesign, Roles.HeadOfFunctional, Roles.HeadOfCoreBanking, Roles.HeadOfInfraDevOps,
    ];

    public ProjectAccessPolicy(IProjectRepository projects, IEngineerRepository engineers, ITeamRepository teams,
        ITaskRepository tasks, IProjectFollowRepository follows)
    {
        _projects = projects;
        _engineers = engineers;
        _teams = teams;
        _tasks = tasks;
        _follows = follows;
    }

    public async Task<bool> CanAccessProjectAsync(Guid projectId, Guid actorId, string actorRole, CancellationToken ct = default)
    {
        // A personal-tasks project (see Project.PersonalOwnerId) is its owner's alone — no role, grant or
        // "unscoped" fallback below reaches it, not even the org-wide ones. Checked first so nothing can
        // short-circuit past it.
        var project = await _projects.GetByIdAsync(projectId, ct);
        if (project?.PersonalOwnerId is Guid personalOwnerId)
            return personalOwnerId == actorId;

        if (GlobalRoles.Contains(actorRole))
            return true;

        // An explicit project-member grant always wins, even for a department head whose project
        // falls outside their own department — being added to a project is a deliberate, specific
        // grant that a coarser department-match default should never override.
        if (await _projects.IsMemberAsync(projectId, actorId, ct))
            return true;

        // Having a task assigned to you in a project is its own standing grant, independent of
        // role or team — GET /projects/mine (ListMyProjectsAsync) already treats this as
        // equivalent to membership when listing "my projects" for the picker that feeds most
        // project-scoped flows, so this method must recognize it too or a project that picker
        // legitimately offered can still come back inaccessible here.
        if (await _tasks.HasAssignedTaskInProjectAsync(actorId, projectId, ct))
            return true;

        // A department head sees their own department's projects, plus any project they've
        // deliberately chosen to follow — but NOT a project merely because one of their engineers
        // happens to be a member of it elsewhere; that's incidental, not something the head opted into.
        if (DepartmentHeadRoles.Contains(actorRole))
            return await IsProjectInActorDepartmentAsync(projectId, actorId, ct)
                || await _follows.GetAsync(actorId, projectId, ct) is not null;

        if (actorRole == Roles.TeamLead)
            return await IsProjectOwnedByLedTeamAsync(projectId, actorId, ct)
                || await ProjectHasLedTeamMemberAsync(projectId, actorId, ct);

        // Individual contributors: on the team that owns the project.
        if (project?.OwnerTeamId is Guid ownerTeamId)
        {
            var actor = await _engineers.GetByIdAsync(actorId, ct);
            if (actor?.TeamId == ownerTeamId)
                return true;
        }

        return false;
    }

    /// <summary>Returns every engineer who can access this project, assembled from the same rules
    /// <see cref="CanAccessProjectAsync"/> checks per-actor — used to build an @mention candidate
    /// list that never suggests someone who couldn't actually see the resulting notification.
    /// Deliberately skips the department-head "follow" grant (<see cref="_follows"/>'s
    /// GetAsync/GetFollowedProjectIdsAsync only look up one actor at a time, and it's a standing
    /// personal bookmark for a handful of heads) — not worth a new repository query for a mention
    /// list that only needs to answer "who's actually working on this."</summary>
    public async Task<IReadOnlyList<Guid>> GetAccessibleEngineerIdsAsync(Guid projectId, CancellationToken ct = default)
    {
        var ids = new HashSet<Guid>();
        var allEngineers = await _engineers.ListActiveAsync(ct);

        foreach (var e in allEngineers.Where(e => GlobalRoles.Contains(e.Role)))
            ids.Add(e.Id);

        foreach (var m in await _projects.ListMembersAsync(projectId, ct))
            ids.Add(m.EngineerId);

        var tasks = await _tasks.GetByProjectAsync(projectId, ct);
        foreach (var assigneeId in tasks.Where(t => t.AssigneeId.HasValue).Select(t => t.AssigneeId!.Value).Distinct())
            ids.Add(assigneeId);

        var project = await _projects.GetByIdAsync(projectId, ct);
        if (project?.OwnerTeamId is Guid ownerTeamId)
        {
            // Everyone on the owning team has access (the IC fallback branch above), not just its
            // formal members — plus the team's lead, who isn't necessarily one of its own Engineer
            // rows (Team.TeamLeadId, not the lead's own Engineer.TeamId, is the source of truth).
            foreach (var e in allEngineers.Where(e => e.TeamId == ownerTeamId))
                ids.Add(e.Id);

            var ownerTeam = await _teams.GetByIdAsync(ownerTeamId, ct);
            if (ownerTeam?.TeamLeadId is Guid leadId)
                ids.Add(leadId);

            if (ownerTeam?.Department is string department)
            {
                foreach (var head in allEngineers.Where(e => DepartmentHeadRoles.Contains(e.Role)))
                {
                    if (head.TeamId is not Guid headTeamId) continue;
                    var headTeam = await _teams.GetByIdAsync(headTeamId, ct);
                    if (headTeam?.Department == department)
                        ids.Add(head.Id);
                }
            }
        }

        return ids.ToList();
    }

    public async Task<bool> CanAccessTeamAsync(Guid teamId, Guid actorId, string actorRole, CancellationToken ct = default)
    {
        if (GlobalRoles.Contains(actorRole))
            return true;

        if (DepartmentHeadRoles.Contains(actorRole))
        {
            var actor = await _engineers.GetByIdAsync(actorId, ct);
            if (actor?.TeamId is not Guid actorTeamId)
                return true; // head with no team is unscoped (mirrors ListTasksQuery)
            var actorTeam = await _teams.GetByIdAsync(actorTeamId, ct);
            if (actorTeam?.Department is not string department)
                return true;
            var team = await _teams.GetByIdAsync(teamId, ct);
            return team?.Department == department;
        }

        if (actorRole == Roles.TeamLead)
        {
            var ledTeam = await DepartmentScope.GetLedTeamAsync(actorId, _teams, ct);
            return ledTeam is null || ledTeam.Id == teamId; // no team led is unscoped (mirrors the department-head branch)
        }

        // Individual contributors: only their own team.
        var ic = await _engineers.GetByIdAsync(actorId, ct);
        return ic?.TeamId == teamId;
    }

    public async Task<bool> CanAccessSprintAsync(Guid sprintId, Guid teamId, Guid actorId, string actorRole, CancellationToken ct = default)
    {
        // Own team / department / global access (covers an engineer's own team's sprints, incl. empty ones).
        if (await CanAccessTeamAsync(teamId, actorId, actorRole, ct))
            return true;

        // Project-based access: visible if the sprint contains a task assigned to the actor, or a task
        // in a project they can access — engineers may be members of projects run by other teams.
        // Uses lightweight queries (EXISTS + distinct project ids) rather than loading task entities.
        if (await _tasks.IsAssignedInSprintAsync(sprintId, actorId, ct))
            return true;

        foreach (var projectId in await _tasks.GetProjectIdsBySprintAsync(sprintId, ct))
            if (await CanAccessProjectAsync(projectId, actorId, actorRole, ct))
                return true;

        return false;
    }

    public async Task<bool> CanAccessTaskAsync(Guid taskId, Guid actorId, string actorRole, CancellationToken ct = default)
    {
        var task = await _tasks.GetByIdAsync(taskId, ct);
        if (task is null)
            return false;

        if (task.AssigneeId == actorId || await CanAccessProjectAsync(task.ProjectId, actorId, actorRole, ct))
            return true;

        // An unassigned QA sub-task (no QA-flagged engineer was available to auto-assign it) would
        // otherwise be inaccessible to everyone but PM+ — let the original task's assignee reach
        // their own unassigned QA task themselves.
        if (task.AssigneeId is null && task.ParentTaskId is Guid parentTaskId)
        {
            var parent = await _tasks.GetByIdAsync(parentTaskId, ct);
            if (parent?.AssigneeId == actorId)
                return true;
        }

        return false;
    }

    public async Task<bool> CanViewTaskAsync(Guid taskId, Guid actorId, string actorRole, CancellationToken ct = default)
    {
        if (await CanAccessTaskAsync(taskId, actorId, actorRole, ct))
            return true;

        if (actorRole != Roles.TeamLead && !DepartmentHeadRoles.Contains(actorRole))
            return false;

        var task = await _tasks.GetByIdAsync(taskId, ct);
        if (task?.AssigneeId is not Guid assigneeId)
            return false;

        var assignee = await _engineers.GetByIdAsync(assigneeId, ct);
        if (assignee?.TeamId is not Guid assigneeTeamId)
            return false;

        if (actorRole == Roles.TeamLead)
        {
            var ledTeam = await DepartmentScope.GetLedTeamAsync(actorId, _teams, ct);
            return ledTeam?.Id == assigneeTeamId;
        }

        var actor = await _engineers.GetByIdAsync(actorId, ct);
        if (actor?.TeamId is not Guid actorTeamId)
            return false;

        var actorTeam = await _teams.GetByIdAsync(actorTeamId, ct);
        var assigneeTeam = await _teams.GetByIdAsync(assigneeTeamId, ct);
        return actorTeam?.Department is not null && actorTeam.Department == assigneeTeam?.Department;
    }

    private async Task<bool> IsProjectInActorDepartmentAsync(Guid projectId, Guid actorId, CancellationToken ct)
    {
        var actor = await _engineers.GetByIdAsync(actorId, ct);
        // A head with no team / a team with no department is unscoped — see all (mirrors ListTasksQuery).
        if (actor?.TeamId is not Guid actorTeamId)
            return true;

        var actorTeam = await _teams.GetByIdAsync(actorTeamId, ct);
        if (actorTeam?.Department is not string department)
            return true;

        var project = await _projects.GetByIdAsync(projectId, ct);
        if (project?.OwnerTeamId is not Guid ownerTeamId)
            return false;

        var ownerTeam = await _teams.GetByIdAsync(ownerTeamId, ct);
        return ownerTeam?.Department == department;
    }

    private async Task<bool> IsProjectOwnedByLedTeamAsync(Guid projectId, Guid actorId, CancellationToken ct)
    {
        var ledTeam = await DepartmentScope.GetLedTeamAsync(actorId, _teams, ct);
        // Not the designated lead of any team is unscoped — see all (mirrors the department-head branch).
        if (ledTeam is null)
            return true;

        var project = await _projects.GetByIdAsync(projectId, ct);
        return project?.OwnerTeamId == ledTeam.Id;
    }

    /// <summary>True if the project has at least one member on the team this actor leads —
    /// lets a team lead reach a project outside their own team that one of their engineers is
    /// actually working on.</summary>
    private async Task<bool> ProjectHasLedTeamMemberAsync(Guid projectId, Guid actorId, CancellationToken ct)
    {
        var ledTeam = await DepartmentScope.GetLedTeamAsync(actorId, _teams, ct);
        if (ledTeam is null) return false; // unscoped case is already handled above

        var teamEngineerIds = (await _engineers.ListAllAsync(ct))
            .Where(e => e.TeamId == ledTeam.Id)
            .Select(e => e.Id)
            .ToHashSet();

        var memberIds = (await _projects.ListMembersAsync(projectId, ct)).Select(m => m.EngineerId);
        return memberIds.Any(teamEngineerIds.Contains);
    }
}
