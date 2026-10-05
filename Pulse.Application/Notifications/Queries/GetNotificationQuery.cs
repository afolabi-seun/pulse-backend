using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Notifications.Queries;

public record GetNotificationQuery(Guid NotificationId, Guid ActorId) : IRequest<ServiceResult<NotificationDto>>;

public class GetNotificationHandler : IRequestHandler<GetNotificationQuery, ServiceResult<NotificationDto>>
{
    private readonly INotificationRepository _notifications;

    public GetNotificationHandler(INotificationRepository notifications) => _notifications = notifications;

    public async Task<ServiceResult<NotificationDto>> Handle(GetNotificationQuery query, CancellationToken ct)
    {
        var notification = await _notifications.GetByIdAsync(query.NotificationId, ct);
        if (notification is null)
            return ServiceResult<NotificationDto>.Fail("NOT_FOUND", $"Notification '{query.NotificationId}' not found.");

        if (notification.UserId != query.ActorId)
            return ServiceResult<NotificationDto>.Fail("FORBIDDEN", "Access denied.");

        return ServiceResult<NotificationDto>.Ok(NotificationDto.From(notification));
    }
}
