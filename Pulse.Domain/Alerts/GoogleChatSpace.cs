using Pulse.Domain.Common;
using Pulse.Domain.Organizations;

namespace Pulse.Domain.Alerts;

/// <summary>A Google Chat space the app has actually been added to, captured from the
/// ADDED_TO_SPACE event Chat sends when that happens. Unlike Slack — where any channel name can be
/// addressed once a bot token exists — posting requires knowing the space's resource name, so there
/// is no free-text equivalent of Slack's "#channel-name" field: an AlertRule can only pick a space
/// that shows up here already.</summary>
public class GoogleChatSpace : Entity
{
    /// <summary>Google Chat's own resource name (e.g. "spaces/AAAAAAAAAAA") — opaque to a person,
    /// which is exactly why DisplayName exists for the UI to show instead.</summary>
    /// <summary>Owning organization. Always the default org until multi-tenancy Phase 2 sets it from
    /// the connecting org.</summary>
    public Guid OrganizationId { get; private set; } = Organization.DefaultId;
    public string SpaceId { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;

    private GoogleChatSpace() { }

    public static GoogleChatSpace Create(string spaceId, string displayName) => new()
    {
        SpaceId = spaceId,
        DisplayName = displayName,
    };

    /// <summary>Chat resends ADDED_TO_SPACE-shaped membership activity over a space's lifetime;
    /// this refreshes the display name (e.g. renamed) rather than creating a duplicate row.</summary>
    public void Refresh(string displayName) => DisplayName = displayName;
}
