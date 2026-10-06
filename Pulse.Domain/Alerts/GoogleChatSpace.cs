using Pulse.Domain.Common;

namespace Pulse.Domain.Alerts;

/// <summary>A Google Chat space the app has actually been added to, captured from the
/// ADDED_TO_SPACE event Chat sends when that happens. Unlike Slack — where any channel name can be
/// addressed once a bot token exists — posting requires knowing the space's resource name, so there
/// is no free-text equivalent of Slack's "#channel-name" field: an AlertRule can only pick a space
/// that shows up here already.
///
/// Every organization shares the one Pulse Chat app, so being added to a space says nothing about whose
/// it is. A new space starts unlinked — invisible to every organization — until a head links it with a
/// one-time code (multi-tenancy Phase 2c, see GoogleChatLinkCode).</summary>
public class GoogleChatSpace : Entity
{
    /// <summary>The organization this space is linked to, or null while unlinked. Spaces registered before
    /// Phase 2c belong to the default organization.</summary>
    public Guid? OrganizationId { get; private set; }
    /// <summary>Google Chat's own resource name (e.g. "spaces/AAAAAAAAAAA") — opaque to a person,
    /// which is exactly why DisplayName exists for the UI to show instead.</summary>
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

    public void LinkTo(Guid organizationId) => OrganizationId = organizationId;

    public void Unlink() => OrganizationId = null;
}
