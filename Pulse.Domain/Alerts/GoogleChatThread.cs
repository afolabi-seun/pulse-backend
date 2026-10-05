using Pulse.Domain.Common;

namespace Pulse.Domain.Alerts;

/// <summary>Google Chat's equivalent of AlertConversation/the Slack thread mapping: maps the thread
/// an alert was posted into (identified by space + Chat's own thread resource name) back to the
/// AlertRule that fired it, so an inbound threaded reply can be resolved to the right scoped tool
/// context. Carries no message transcript, same reasoning as AlertConversation — each follow-up is
/// a fresh, re-grounded lookup.</summary>
public class GoogleChatThread : Entity
{
    public Guid AlertRuleId { get; private set; }
    public string SpaceId { get; private set; } = string.Empty;
    /// <summary>Chat's thread resource name (e.g. "spaces/AAAA/threads/BBBB") — the anchor a later
    /// reply's own thread.name is compared against, playing the same role Slack's thread_ts does.</summary>
    public string ThreadName { get; private set; } = string.Empty;

    private GoogleChatThread() { }

    public static GoogleChatThread Create(Guid alertRuleId, string spaceId, string threadName) => new()
    {
        AlertRuleId = alertRuleId,
        SpaceId = spaceId,
        ThreadName = threadName,
    };
}
