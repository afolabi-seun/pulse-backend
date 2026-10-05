namespace Pulse.Application.Projects;

/// <summary>
/// Derives a short, uppercase project code from a project's name (e.g. "Notifications Platform"
/// → "NOTIFIC"), for projects that don't get an explicit one from the caller — CSV backlog import
/// auto-creates projects with no user interaction to ask for a code, and existing projects need
/// one backfilled. Matches <c>Project</c>'s own format invariant (2-10 uppercase letters/digits,
/// starting with a letter).
/// </summary>
public static class ProjectCodeGenerator
{
    public const int MaxBaseLength = 6;

    public static string DeriveBase(string name)
    {
        var letters = new string(name.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (letters.Length == 0)
            return "PROJ";
        return letters.Length <= MaxBaseLength ? letters : letters[..MaxBaseLength];
    }

    /// <summary>Returns <paramref name="baseCode"/> if not already in <paramref name="existingCodes"/>,
    /// otherwise appends the smallest integer suffix that is. Adds whichever code it returns to
    /// <paramref name="existingCodes"/>, so a caller generating several codes in the same pass (e.g.
    /// backfilling every project in one migration, or importing a CSV that creates several new
    /// projects) can pass the same set through repeated calls without colliding with codes this
    /// same batch already handed out.</summary>
    public static string MakeUnique(string baseCode, ISet<string> existingCodes)
    {
        if (existingCodes.Add(baseCode))
            return baseCode;

        for (var i = 2; ; i++)
        {
            var candidate = $"{baseCode}{i}";
            if (existingCodes.Add(candidate))
                return candidate;
        }
    }
}
