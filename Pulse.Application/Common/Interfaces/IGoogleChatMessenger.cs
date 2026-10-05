namespace Pulse.Application.Common.Interfaces;

/// <summary>Posts into a Google Chat space the app has already been added to (see
/// GoogleChatSpace) — the Application layer's abstraction over the Chat REST API, kept
/// implementation-agnostic the same way ISlackClient hides the raw Slack Web API.</summary>
public interface IGoogleChatMessenger
{
    /// <summary>Posts <paramref name="text"/> into <paramref name="spaceId"/>, either as a new
    /// thread (<paramref name="threadName"/> null) or threaded under an earlier one. Returns the
    /// new (or continued) thread's resource name — a later reply threads against it the same way a
    /// Slack reply threads against a message's ts — or null when nothing could be posted: no
    /// credentials configured, or the call failed. Never throws.</summary>
    Task<string?> PostToSpaceAsync(string spaceId, string text, string? threadName = null, CancellationToken ct = default);
}
