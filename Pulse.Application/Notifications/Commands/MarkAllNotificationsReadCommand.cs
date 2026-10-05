using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Notifications.Commands;

public record MarkAllNotificationsReadCommand(Guid UserId) : IRequest<ServiceResult<bool>>;

public class MarkAllNotificationsReadHandler : IRequestHandler<MarkAllNotificationsReadCommand, ServiceResult<bool>>
{
    private readonly INotificationRepository _notifications;

    public MarkAllNotificationsReadHandler(INotificationRepository notifications) =>
        _notifications = notifications;

    public async Task<ServiceResult<bool>> Handle(MarkAllNotificationsReadCommand cmd, CancellationToken ct)
    {
        await _notifications.MarkAllReadForUserAsync(cmd.UserId, ct);
        return ServiceResult<bool>.Ok(true);
    }
}
