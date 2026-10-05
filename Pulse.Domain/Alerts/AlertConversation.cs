using Pulse.Domain.Common;

namespace Pulse.Domain.Alerts;

/// <summary>Maps a Slack thread back to the AlertRule that started it, so a reply in that thread can
/// be answered with the same scoped tools the original explanation used. Created only when a rule
/// fires via chat.postMessage (bot-token delivery) — plain incoming-webhook delivery never creates
/// one, since it has no thread ts to track. Carries no message transcript: each follow-up is a
/// fresh, freshly-grounded tool lookup (see IAlertExplainer.AnswerFollowUpAsync), not a replay of
/// the original conversation — deliberately simpler than persisting a full message history.</summary>
public class AlertConversation : Entity
{
    public Guid AlertRuleId { get; private set; }
    public string ChannelId { get; private set; } = string.Empty;
    public string ThreadTs { get; private set; } = string.Empty;

    private AlertConversation() { }

    public static AlertConversation Create(Guid alertRuleId, string channelId, string threadTs) => new()
    {
        AlertRuleId = alertRuleId,
        ChannelId = channelId,
        ThreadTs = threadTs,
    };
}
