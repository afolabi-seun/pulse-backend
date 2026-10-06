using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Organizations;

namespace Pulse.Application.Alerts;

/// <summary>
/// Which chat apps an organization may route alerts to. Slack: an org that has connected its own workspace
/// (multi-tenancy Phase 2b), or the default org, which still has the deployment's SLACK_BOT_TOKEN. Google
/// Chat: only a space linked to the organization (Phase 2c) — the space lookup is org-filtered, so another
/// organization's space, or an unlinked one, isn't found.
/// </summary>
public static class ChatDeliveryAvailability
{
    public const string SlackUnavailableMessage =
        "Connect your organization's Slack workspace (Integrations) before sending alerts to Slack.";
    public const string GoogleChatUnavailableMessage =
        "That Google Chat space isn't linked to your organization. Link it under Admin → Integrations first.";

    private static bool IsDefaultOrganization(ICurrentUserService currentUser) =>
        (currentUser.OrganizationId ?? Organization.DefaultId) == Organization.DefaultId;

    /// <summary>The error to return, or null when the rule may use what it asks for.</summary>
    public static async Task<string?> CheckAsync(ICurrentUserService currentUser, ISlackInstallationRepository slack,
        IGoogleChatSpaceRepository googleChatSpaces, string? slackChannel, string? googleChatSpaceId, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(googleChatSpaceId)
            && (await googleChatSpaces.GetBySpaceIdAsync(googleChatSpaceId.Trim(), ct))?.OrganizationId is null)
            return GoogleChatUnavailableMessage;

        if (!string.IsNullOrWhiteSpace(slackChannel) && !IsDefaultOrganization(currentUser)
            && (currentUser.OrganizationId is not Guid orgId || await slack.GetByOrganizationAsync(orgId, ct) is null))
            return SlackUnavailableMessage;

        return null;
    }
}
