using Pulse.Domain.Common;

namespace Pulse.Domain.Notifications;

public class Notification : Entity
{
    public Guid UserId { get; private set; }
    public string Kind { get; private set; } = string.Empty;
    public string? Payload { get; private set; }
    public string Channel { get; private set; } = NotificationChannel.InApp;
    public DateTime? ReadAt { get; private set; }

    public bool IsRead => ReadAt.HasValue;

    private Notification() { }

    public static Notification Create(Guid userId, string kind, string? payload = null, string channel = NotificationChannel.InApp) =>
        new()
        {
            UserId = userId,
            Kind = kind,
            Payload = payload,
            Channel = channel
        };

    public void MarkRead()
    {
        if (!IsRead)
            ReadAt = DateTime.UtcNow;
    }
}
