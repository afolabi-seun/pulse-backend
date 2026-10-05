using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Notifications;
using MediatR;

namespace Pulse.Application.Notifications.Commands;

public record CreateNotificationCommand(
    Guid UserId,
    string Kind,
    string? Payload = null,
    string Channel = NotificationChannel.InApp) : IRequest<ServiceResult<NotificationDto>>;

public class CreateNotificationHandler : IRequestHandler<CreateNotificationCommand, ServiceResult<NotificationDto>>
{
    private readonly INotificationRepository _notifications;
    private readonly IRealtimeNotifier _realtime;

    public CreateNotificationHandler(INotificationRepository notifications, IRealtimeNotifier realtime)
    {
        _notifications = notifications;
        _realtime = realtime;
    }

    public async Task<ServiceResult<NotificationDto>> Handle(CreateNotificationCommand cmd, CancellationToken ct)
    {
        var notification = Notification.Create(cmd.UserId, cmd.Kind, cmd.Payload, cmd.Channel);

        await _notifications.AddAsync(notification, ct);
        await _notifications.SaveChangesAsync(ct);

        var dto = NotificationDto.From(notification);
        await _realtime.SendNotificationAsync(cmd.UserId, dto, ct);

        return ServiceResult<NotificationDto>.Ok(dto);
    }
}
