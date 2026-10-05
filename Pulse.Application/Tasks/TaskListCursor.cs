using System.Globalization;
using System.Text;

namespace Pulse.Application.Tasks;

/// <summary>
/// Opaque keyset-pagination cursor for the task list: a base64 of
/// "{isActiveGroup}|{activatedAt|null}|{dueDate|null}|{taskId}". The list sorts Active tasks
/// first (by ActivatedAt descending), then every other status (by due date ascending, undated
/// last) — so a cursor row carries only the field its own group actually sorts by; the other one
/// is encoded as "null" and ignored on decode.
/// Decoding is total — malformed client input (including a cursor from before this two-group
/// format existed) returns false rather than throwing, so a bad or stale cursor surfaces as a
/// validation error instead of an unhandled 500.
/// </summary>
public static class TaskListCursor
{
    private const string RoundTrip = "O";

    public static string Encode(bool isActiveGroup, DateTime? activatedAt, DateOnly? dueDate, Guid id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join('|',
            isActiveGroup ? "1" : "0",
            activatedAt.HasValue ? activatedAt.Value.ToString(RoundTrip, CultureInfo.InvariantCulture) : "null",
            dueDate.HasValue ? dueDate.Value.ToString(RoundTrip, CultureInfo.InvariantCulture) : "null",
            id)));

    public static bool TryDecode(string cursor, out bool isActiveGroup, out DateTime? activatedAt, out DateOnly? dueDate, out Guid id)
    {
        isActiveGroup = false;
        activatedAt = null;
        dueDate = null;
        id = Guid.Empty;
        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var parts = raw.Split('|');
            if (parts.Length != 4) return false;

            isActiveGroup = parts[0] == "1";

            if (parts[1] != "null")
            {
                if (!DateTime.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var a)) return false;
                activatedAt = a;
            }

            if (parts[2] != "null")
            {
                if (!DateOnly.TryParse(parts[2], CultureInfo.InvariantCulture, out var d)) return false;
                dueDate = d;
            }

            return Guid.TryParse(parts[3], out id);
        }
        catch (FormatException)
        {
            return false; // not valid base64
        }
    }

    /// <summary>Cursor for an explicitly-sorted list request: a base64 of
    /// "{sortBy}|{direction}|{value|null}|{taskId}" — one flat order by a single column
    /// (tiebroken by id) across every task, independent of the default two-group cursor above.
    /// Kept as a fully separate encode/decode pair so the default (no sort chosen) path is never
    /// touched by this one.</summary>
    public static string EncodeSorted(string sortBy, string direction, string? value, Guid id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join('|',
            sortBy, direction, value ?? "null", id)));

    public static bool TryDecodeSorted(string cursor, out string sortBy, out string direction, out string? value, out Guid id)
    {
        sortBy = "";
        direction = "";
        value = null;
        id = Guid.Empty;
        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var parts = raw.Split('|');
            if (parts.Length != 4) return false;

            sortBy = parts[0];
            direction = parts[1];
            value = parts[2] == "null" ? null : parts[2];

            return Guid.TryParse(parts[3], out id);
        }
        catch (FormatException)
        {
            return false; // not valid base64
        }
    }
}
