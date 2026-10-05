using Pulse.Domain.Notifications;

namespace Pulse.Application.Notifications;

public record NotificationDto(
    Guid Id,
    Guid UserId,
    string Kind,
    string? Payload,
    string Channel,
    bool IsRead,
    DateTime? ReadAt,
    DateTime SentAt)
{
    public static NotificationDto From(Notification n) =>
        new(n.Id, n.UserId, n.Kind, n.Payload, n.Channel, n.IsRead, n.ReadAt, n.CreatedAt);
}
