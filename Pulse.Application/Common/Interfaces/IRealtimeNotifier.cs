using Pulse.Application.Notifications;
using Pulse.Application.Tasks;

namespace Pulse.Application.Common.Interfaces;

public interface IRealtimeNotifier
{
    /// <summary>Push a notification payload to a specific user's connected clients.</summary>
    Task SendNotificationAsync(Guid userId, NotificationDto notification, CancellationToken ct = default);

    /// <summary>Push an updated task to the task's assignee.</summary>
    Task SendTaskUpdatedAsync(Guid assigneeId, TaskDto task, CancellationToken ct = default);

    /// <summary>
    /// Tells an already-connected client that its role changed, so it re-fetches GET /auth/me
    /// instead of running with a stale capability set until its next token refresh.
    /// </summary>
    Task SendRoleChangedAsync(Guid userId, CancellationToken ct = default);
}
