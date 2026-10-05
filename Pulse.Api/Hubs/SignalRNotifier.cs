using Pulse.Application.Common.Interfaces;
using Pulse.Application.Notifications;
using Pulse.Application.Tasks;
using Microsoft.AspNetCore.SignalR;

namespace Pulse.Api.Hubs;

/// <summary>
/// Adapts IRealtimeNotifier to the SignalR hub. Lives in the API project because
/// IHubContext requires the concrete hub type, which is also in the API project.
/// </summary>
public class SignalRNotifier : IRealtimeNotifier
{
    private readonly IHubContext<PulseHub> _hub;

    public SignalRNotifier(IHubContext<PulseHub> hub) => _hub = hub;

    public Task SendNotificationAsync(Guid userId, NotificationDto notification, CancellationToken ct) =>
        _hub.Clients.User(userId.ToString()).SendAsync("notification.received", notification, ct);

    public Task SendTaskUpdatedAsync(Guid assigneeId, TaskDto task, CancellationToken ct) =>
        _hub.Clients.User(assigneeId.ToString()).SendAsync("task.updated", task, ct);

    public Task SendRoleChangedAsync(Guid userId, CancellationToken ct) =>
        _hub.Clients.User(userId.ToString()).SendAsync("role.changed", ct);
}
