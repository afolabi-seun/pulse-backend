namespace Pulse.Domain.Notifications;

/// <summary>Where a person's notifications are also sent as a personal chat message, if anywhere.</summary>
public static class ChatChannel
{
    public const string None = "none";
    public const string Slack = "slack";
    public const string GoogleChat = "google_chat";

    public static readonly IReadOnlyList<string> All = [None, Slack, GoogleChat];
}

/// <summary>
/// One person's personal chat delivery: the channel they chose (opt-in — <see cref="ChatChannel.None"/> by
/// default) and how to reach them there. The Slack user id is looked up by email in their organization's
/// connected workspace; the Google Chat direct-message space is captured when they open a DM with Pulse.
/// </summary>
public class PersonalChatSettings
{
    public Guid EngineerId { get; private set; }
    public string Channel { get; private set; } = ChatChannel.None;
    public string? SlackUserId { get; private set; }
    public string? GoogleChatDmSpace { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    private PersonalChatSettings() { }

    public static PersonalChatSettings For(Guid engineerId) => new() { EngineerId = engineerId, UpdatedAt = DateTime.UtcNow };

    public void Choose(string channel)
    {
        if (!ChatChannel.All.Contains(channel))
            throw new ArgumentException($"Unknown chat channel '{channel}'.", nameof(channel));
        Channel = channel;
        UpdatedAt = DateTime.UtcNow;
    }

    public void SetSlackUser(string? slackUserId)
    {
        SlackUserId = slackUserId;
        UpdatedAt = DateTime.UtcNow;
    }

    public void SetGoogleChatDm(string? dmSpace)
    {
        GoogleChatDmSpace = dmSpace;
        UpdatedAt = DateTime.UtcNow;
    }
}
