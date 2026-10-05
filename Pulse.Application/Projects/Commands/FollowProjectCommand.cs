using System.Text.Json;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using Pulse.Domain.Notifications;
using Pulse.Domain.Projects;
using MediatR;

namespace Pulse.Application.Projects.Commands;

public record FollowProjectCommand(Guid ProjectId, Guid FollowerId, string ActorRole = "") : IRequest<ServiceResult<bool>>;

public class FollowProjectHandler : IRequestHandler<FollowProjectCommand, ServiceResult<bool>>
{
    private readonly IProjectRepository _projects;
    private readonly IProjectFollowRepository _follows;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;
    private readonly INotificationRepository _notifications;
    private readonly IAuditLogRepository _audit;

    public FollowProjectHandler(
        IProjectRepository projects,
        IProjectFollowRepository follows,
        IEngineerRepository engineers,
        ITeamRepository teams,
        INotificationRepository notifications,
        IAuditLogRepository audit)
    {
        _projects      = projects;
        _follows       = follows;
        _engineers     = engineers;
        _teams         = teams;
        _notifications = notifications;
        _audit         = audit;
    }

    public async Task<ServiceResult<bool>> Handle(FollowProjectCommand cmd, CancellationToken ct)
    {
        var project = await _projects.GetByIdAsync(cmd.ProjectId, ct);
        if (project is null)
            return ServiceResult<bool>.Fail("NOT_FOUND", $"Project '{cmd.ProjectId}' not found.");

        var existing = await _follows.GetAsync(cmd.FollowerId, cmd.ProjectId, ct);
        if (existing is not null)
            return ServiceResult<bool>.Ok(true); // idempotent

        var follower = await _engineers.GetByIdAsync(cmd.FollowerId, ct);
        if (follower is null)
            return ServiceResult<bool>.Fail("NOT_FOUND", "Follower engineer not found.");

        // A department-scoped head may follow a project only if one of their department's engineers
        // is a member of it. Org-wide roles (PMO/Product heads, ProjectManager, ProductManager)
        // have no single department to scope this check to, so they're exempt — matches
        // ProjectAccessPolicy.GlobalRoles, which already treats these four the same way.
        if (cmd.ActorRole is not Roles.HeadOfPmo and not Roles.HeadOfProduct
            and not Roles.ProjectManager and not Roles.ProductManager
            && !await ProjectHasDepartmentMemberAsync(cmd.ProjectId, cmd.FollowerId, ct))
            return ServiceResult<bool>.Fail("FORBIDDEN",
                "You can only follow projects your department's engineers are members of.");

        await _follows.AddAsync(ProjectFollow.Create(cmd.FollowerId, cmd.ProjectId), ct);
        await _follows.SaveChangesAsync(ct);

        await _audit.LogAsync("PROJECT_FOLLOWED", cmd.FollowerId, null,
            $"Head '{follower.Name}' is now following project '{project.Name}'", ct);

        // Notify team leads who have engineers actively working on this project.
        var teamLeadIds = await _follows.GetTeamLeadIdsForProjectAsync(cmd.ProjectId, ct);
        var payload = JsonSerializer.Serialize(new { followerName = follower.Name, projectName = project.Name });
        foreach (var leadId in teamLeadIds)
        {
            await _notifications.AddAsync(Notification.Create(
                leadId,
                "PROJECT_FOLLOW_STARTED",
                payload), ct);
        }

        if (teamLeadIds.Count > 0)
            await _notifications.SaveChangesAsync(ct);

        return ServiceResult<bool>.Ok(true);
    }

    /// <summary>True if the project has at least one member who is an engineer in the follower's department.</summary>
    private async Task<bool> ProjectHasDepartmentMemberAsync(Guid projectId, Guid followerId, CancellationToken ct)
    {
        var deptEngineerIds = await DepartmentScope.EngineerIdsAsync(followerId, _engineers, _teams, ct);
        if (deptEngineerIds is null) return true; // unscoped head (no team/department)

        var memberIds = (await _projects.ListMembersAsync(projectId, ct)).Select(m => m.EngineerId);
        return memberIds.Any(deptEngineerIds.Contains);
    }
}
