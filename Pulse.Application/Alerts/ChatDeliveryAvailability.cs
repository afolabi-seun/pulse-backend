using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Organizations;

namespace Pulse.Application.Alerts;

/// <summary>
/// Which chat apps an organization may route alerts to. Slack: an org that has connected its own workspace
/// (multi-tenancy Phase 2b), or the default org, which still has the deployment's SLACK_BOT_TOKEN. Google
/// Chat still runs on the deployment's own service account, the default org's, until each org links its
/// own spaces (Phase 2c) — otherwise another org's alerts would be posted into the default org's spaces.
/// </summary>
public static class ChatDeliveryAvailability
{
    public const string SlackUnavailableMessage =
        "Connect your organization's Slack workspace (Integrations) before sending alerts to Slack.";
    public const string GoogleChatUnavailableMessage =
        "Google Chat delivery isn't available for your organization yet. Use in-app, email, webhook or Slack delivery.";

    private static bool IsDefaultOrganization(ICurrentUserService currentUser) =>
        (currentUser.OrganizationId ?? Organization.DefaultId) == Organization.DefaultId;

    /// <summary>The error to return, or null when the rule may use what it asks for.</summary>
    public static async Task<string?> CheckAsync(ICurrentUserService currentUser, ISlackInstallationRepository slack,
        string? slackChannel, string? googleChatSpaceId, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(googleChatSpaceId) && !IsDefaultOrganization(currentUser))
            return GoogleChatUnavailableMessage;

        if (!string.IsNullOrWhiteSpace(slackChannel) && !IsDefaultOrganization(currentUser)
            && (currentUser.OrganizationId is not Guid orgId || await slack.GetByOrganizationAsync(orgId, ct) is null))
            return SlackUnavailableMessage;

        return null;
    }
}
