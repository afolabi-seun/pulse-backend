using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Notifications.Commands;

public record MarkNotificationReadCommand(Guid NotificationId, Guid ActorId) : IRequest<ServiceResult<NotificationDto>>;

public class MarkNotificationReadHandler : IRequestHandler<MarkNotificationReadCommand, ServiceResult<NotificationDto>>
{
    private readonly INotificationRepository _notifications;

    public MarkNotificationReadHandler(INotificationRepository notifications) => _notifications = notifications;

    public async Task<ServiceResult<NotificationDto>> Handle(MarkNotificationReadCommand cmd, CancellationToken ct)
    {
        var notification = await _notifications.GetByIdAsync(cmd.NotificationId, ct);
        if (notification is null)
            return ServiceResult<NotificationDto>.Fail("NOT_FOUND", $"Notification '{cmd.NotificationId}' not found.");

        if (notification.UserId != cmd.ActorId)
            return ServiceResult<NotificationDto>.Fail("FORBIDDEN", "Cannot mark another user's notification as read.");

        notification.MarkRead();
        await _notifications.SaveChangesAsync(ct);

        return ServiceResult<NotificationDto>.Ok(NotificationDto.From(notification));
    }
}
