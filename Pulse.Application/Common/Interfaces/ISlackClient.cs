namespace Pulse.Application.Common.Interfaces;

/// <summary>Posts via the Slack Web API (chat.postMessage), as opposed to an incoming webhook —
/// needed specifically because a webhook can't return a message ts to reply into later, so it can't
/// back a follow-up conversation the way this can. Only usable when a Slack bot token is
/// configured; every method degrades to null rather than throwing when it isn't.</summary>
public interface ISlackClient
{
    /// <summary>Returns the channel and message ts on success (persist these to track the thread for
    /// follow-ups), or null on any failure — including no bot token configured. A null threadTs
    /// starts a new top-level message; passing one replies into an existing thread.</summary>
    Task<(string ChannelId, string Ts)?> PostMessageAsync(string channel, string text, string? threadTs = null, CancellationToken ct = default);
}
