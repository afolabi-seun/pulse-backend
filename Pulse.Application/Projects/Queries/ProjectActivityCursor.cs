using System.Text;

namespace Pulse.Application.Projects.Queries;

/// <summary>
/// Opaque keyset-pagination cursor for a project's activity feed: a base64 of "{changedAt:O}|{id}".
/// Mirrors Pulse.Application.Tasks.TaskListCursor's shape — a tiebreaker on Id is required
/// because multiple TaskHistory rows from the same update transaction can share an identical
/// ChangedAt timestamp, so ChangedAt alone isn't a stable sort key at page boundaries. Decoding
/// is total — malformed client input returns false rather than throwing.
/// </summary>
public static class ProjectActivityCursor
{
    public static string Encode(DateTime changedAt, Guid id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{changedAt:O}|{id}"));

    public static bool TryDecode(string cursor, out DateTime changedAt, out Guid id)
    {
        changedAt = default;
        id = Guid.Empty;
        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var idx = raw.LastIndexOf('|');
            if (idx < 0) return false;

            if (!DateTime.TryParse(raw[..idx], null, System.Globalization.DateTimeStyles.RoundtripKind, out changedAt))
                return false;

            return Guid.TryParse(raw[(idx + 1)..], out id);
        }
        catch (FormatException)
        {
            return false; // not valid base64
        }
    }
}
