using System.Globalization;

namespace Pulse.Application.Tasks;

/// <summary>
/// Reads the due dates people put in an import CSV. Deliberately NOT <c>DateOnly.TryParse</c>: that uses the server's culture, so on an
/// invariant-culture server "16/10/2026" failed outright (month 16) while "09/10/2026" quietly became 10 September. Here the day always comes
/// first for slash and dash dates, and anything else that is not an unambiguous year-first ISO date is refused rather than guessed at.
/// </summary>
public static class ImportDateParser
{
    private static readonly string[] Formats =
    [
        "yyyy-MM-dd", "yyyy/MM/dd", "yyyy-M-d", "yyyy/M/d",
        "dd/MM/yyyy", "d/M/yyyy",
        "dd-MM-yyyy", "d-M-yyyy",
        "dd.MM.yyyy", "d.M.yyyy",
        "d MMM yyyy", "dd MMM yyyy", "d MMMM yyyy", "dd MMMM yyyy",
        "d-MMM-yyyy", "dd-MMM-yyyy",
    ];

    public static bool TryParse(string? text, out DateOnly date) =>
        DateOnly.TryParseExact(text?.Trim(), Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    public const string Hint = "Use YYYY-MM-DD or DD/MM/YYYY.";
}
