using Pulse.Domain.Engineers;

namespace Pulse.Application.Common;

/// <summary>
/// Finds "@Full Name" mentions in free text (a comment body, a blocker reason). Matches against
/// a known candidate list rather than a generic "@word" regex — scoping candidates to the task's
/// project members (see call sites) keeps this both unambiguous (matching a real person's exact
/// name, not an arbitrary token) and meaningful (you can only mention someone actually in the
/// room, the same boundary every other cross-person action in this codebase already respects).
/// </summary>
public static class MentionParser
{
    public static IReadOnlyList<Engineer> ExtractMentions(string text, IEnumerable<Engineer> candidates, Guid excludeEngineerId)
    {
        var found = new List<Engineer>();
        foreach (var candidate in candidates)
        {
            if (candidate.Id == excludeEngineerId) continue; // mentioning yourself notifies no one
            if (text.Contains("@" + candidate.Name, StringComparison.OrdinalIgnoreCase))
                found.Add(candidate);
        }
        return found;
    }
}
