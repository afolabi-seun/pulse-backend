using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Organizations;

namespace Pulse.Application.Alerts;

/// <summary>
/// Slack and Google Chat delivery still run on the deployment's own credentials (SLACK_BOT_TOKEN, the
/// Google Chat service account), which belong to the default organization's workspace. Until each org
/// connects its own (multi-tenancy Phases 2b/2c), only the default org may route alerts to them —
/// otherwise another org's alerts would be posted into the default org's Slack.
/// </summary>
public static class ChatDeliveryAvailability
{
    public const string UnavailableMessage =
        "Slack and Google Chat delivery aren't available for your organization yet. Use in-app, email or webhook delivery.";

    public static bool IsAvailableFor(ICurrentUserService currentUser) =>
        (currentUser.OrganizationId ?? Organization.DefaultId) == Organization.DefaultId;

    /// <summary>The error to return, or null when the rule may use what it asks for.</summary>
    public static string? Check(ICurrentUserService currentUser, string? slackChannel, string? googleChatSpaceId) =>
        (!string.IsNullOrWhiteSpace(slackChannel) || !string.IsNullOrWhiteSpace(googleChatSpaceId)) && !IsAvailableFor(currentUser)
            ? UnavailableMessage
            : null;
}
