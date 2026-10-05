using System.Text.Json;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Notifications;
using MediatR;

namespace Pulse.Application.Projects.Commands;

public record UnfollowProjectCommand(Guid ProjectId, Guid FollowerId) : IRequest<ServiceResult<bool>>;

public class UnfollowProjectHandler : IRequestHandler<UnfollowProjectCommand, ServiceResult<bool>>
{
    private readonly IProjectRepository _projects;
    private readonly IProjectFollowRepository _follows;
    private readonly IEngineerRepository _engineers;
    private readonly INotificationRepository _notifications;
    private readonly IAuditLogRepository _audit;

    public UnfollowProjectHandler(
        IProjectRepository projects,
        IProjectFollowRepository follows,
        IEngineerRepository engineers,
        INotificationRepository notifications,
        IAuditLogRepository audit)
    {
        _projects      = projects;
        _follows       = follows;
        _engineers     = engineers;
        _notifications = notifications;
        _audit         = audit;
    }

    public async Task<ServiceResult<bool>> Handle(UnfollowProjectCommand cmd, CancellationToken ct)
    {
        var follow = await _follows.GetAsync(cmd.FollowerId, cmd.ProjectId, ct);
        if (follow is null)
            return ServiceResult<bool>.Ok(false); // idempotent — already not following

        var project  = await _projects.GetByIdAsync(cmd.ProjectId, ct);
        var follower = await _engineers.GetByIdAsync(cmd.FollowerId, ct);

        _follows.Remove(follow);
        await _follows.SaveChangesAsync(ct);

        await _audit.LogAsync("PROJECT_UNFOLLOWED", cmd.FollowerId, null,
            $"Head '{follower?.Name}' stopped following project '{project?.Name}'", ct);

        var teamLeadIds = await _follows.GetTeamLeadIdsForProjectAsync(cmd.ProjectId, ct);
        var payload = JsonSerializer.Serialize(new { followerName = follower?.Name ?? "Head", projectName = project?.Name ?? "the project" });
        foreach (var leadId in teamLeadIds)
        {
            await _notifications.AddAsync(Notification.Create(
                leadId,
                "PROJECT_FOLLOW_ENDED",
                payload), ct);
        }

        if (teamLeadIds.Count > 0)
            await _notifications.SaveChangesAsync(ct);

        return ServiceResult<bool>.Ok(true);
    }
}
