using System.Text.Json;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Notifications;

namespace Pulse.Application.Notifications;

/// <summary>An email to send alongside a notification, built by the caller (it knows the context).</summary>
public record NotificationEmail(string To, string Subject, string HtmlBody);

/// <summary>
/// The one way to notify someone. Records the notification in their in-app inbox, pushes it to any open
/// session, and sends the email — unless they've switched email off for that kind (security notices can't
/// be). Chat delivery joins here next (personal Slack / Google Chat messages).
/// </summary>
public interface INotificationDispatcher
{
    /// <param name="payload">Serialized as JSON for the inbox, unless it's already a string.</param>
    Task NotifyAsync(Guid recipientId, string kind, object? payload, NotificationEmail? email = null, CancellationToken ct = default);

    /// <summary>Just the email, still subject to the recipient's preference for <paramref name="kind"/> — for
    /// sites that email someone without putting anything in their inbox.</summary>
    Task EmailAsync(Guid recipientId, string kind, NotificationEmail email, CancellationToken ct = default);
}

public class NotificationDispatcher : INotificationDispatcher
{
    private readonly INotificationRepository _notifications;
    private readonly IRealtimeNotifier _realtime;
    private readonly IEmailQueue _email;
    private readonly INotificationPreferenceRepository _preferences;

    public NotificationDispatcher(INotificationRepository notifications, IRealtimeNotifier realtime, IEmailQueue email,
        INotificationPreferenceRepository preferences)
    {
        _notifications = notifications;
        _realtime = realtime;
        _email = email;
        _preferences = preferences;
    }

    public async Task NotifyAsync(Guid recipientId, string kind, object? payload, NotificationEmail? email = null, CancellationToken ct = default)
    {
        var serialized = payload switch
        {
            null => null,
            string s => s,
            _ => JsonSerializer.Serialize(payload),
        };
        var notification = Notification.Create(recipientId, kind, serialized, NotificationChannel.InApp);
        await _notifications.AddAsync(notification, ct);
        await _notifications.SaveChangesAsync(ct);
        await _realtime.SendNotificationAsync(recipientId, NotificationDto.From(notification), ct);

        if (email is not null)
            await EmailAsync(recipientId, kind, email, ct);
    }

    public async Task EmailAsync(Guid recipientId, string kind, NotificationEmail email, CancellationToken ct = default)
    {
        if (!NotificationCatalog.IsMandatory(kind)
            && await _preferences.GetAsync(recipientId, kind, ct) is { EmailEnabled: false })
            return;

        _email.Enqueue(email.To, email.Subject, email.HtmlBody);
    }
}
