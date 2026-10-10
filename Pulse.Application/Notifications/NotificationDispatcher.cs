using System.Text.Json;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Notifications;

namespace Pulse.Application.Notifications;

/// <summary>An email to send alongside a notification, built by the caller (it knows the context).</summary>
public record NotificationEmail(string To, string Subject, string HtmlBody);

/// <summary>
/// The one way to notify someone. Records the notification in their in-app inbox, pushes it to any open
/// session, sends the email — unless they've switched email off for that kind (security notices can't be) —
/// and, for people who chose a personal chat channel, queues it as a Slack / Google Chat direct message
/// unless they've switched chat off for that kind. Chat mirrors the inbox: email-only sends don't post to chat,
/// so a site that notifies and emails in two calls doesn't message anyone twice.
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
    private readonly IPersonalChatSettingsRepository _chatSettings;
    private readonly IChatNotificationQueue _chat;
    private readonly IAppSettings _settings;

    public NotificationDispatcher(INotificationRepository notifications, IRealtimeNotifier realtime, IEmailQueue email,
        INotificationPreferenceRepository preferences, IPersonalChatSettingsRepository chatSettings, IChatNotificationQueue chat,
        IAppSettings settings)
    {
        _notifications = notifications;
        _realtime = realtime;
        _email = email;
        _preferences = preferences;
        _chatSettings = chatSettings;
        _chat = chat;
        _settings = settings;
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

        if (await _chatSettings.GetAsync(recipientId, ct) is { Channel: not ChatChannel.None }
            && await _preferences.GetAsync(recipientId, kind, ct) is not { ChatEnabled: false })
        {
            var headline = email?.Subject ?? NotificationCatalog.Find(kind)?.Label ?? kind;
            _chat.Enqueue(recipientId, $"{headline}\n{_settings.AppBaseUrl}/notifications");
        }
    }

    public async Task EmailAsync(Guid recipientId, string kind, NotificationEmail email, CancellationToken ct = default)
    {
        if (!NotificationCatalog.IsMandatory(kind)
            && await _preferences.GetAsync(recipientId, kind, ct) is { EmailEnabled: false })
            return;

        _email.Enqueue(email.To, email.Subject, email.HtmlBody);
    }
}
