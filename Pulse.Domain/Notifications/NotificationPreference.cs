namespace Pulse.Domain.Notifications;

/// <summary>
/// One person's override for one notification kind. Only overrides are stored: no row means the default
/// (email on). The in-app inbox isn't configurable — it always records every notification.
/// </summary>
public class NotificationPreference
{
    public Guid EngineerId { get; private set; }
    public string Kind { get; private set; } = string.Empty;
    public bool EmailEnabled { get; private set; } = true;
    public DateTime UpdatedAt { get; private set; }

    private NotificationPreference() { }

    public static NotificationPreference Create(Guid engineerId, string kind, bool emailEnabled) => new()
    {
        EngineerId = engineerId,
        Kind = kind,
        EmailEnabled = emailEnabled,
        UpdatedAt = DateTime.UtcNow,
    };

    public void SetEmail(bool enabled)
    {
        EmailEnabled = enabled;
        UpdatedAt = DateTime.UtcNow;
    }
}
