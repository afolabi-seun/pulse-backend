namespace Pulse.Domain.Projects;

/// <summary>
/// The kinds of wiki page, so documents can be grouped by what they are rather than only by project. A fixed list kept in one place: adding a kind
/// is one line here plus its label in the frontend. Stored as the lower-case value; <see cref="General"/> is what a page has when nobody chose.
/// </summary>
public static class WikiCategory
{
    public const string Development  = "development";
    public const string Architecture = "architecture";
    public const string Product      = "product";
    public const string Pmo          = "pmo";
    public const string Functional   = "functional";
    public const string General      = "general";

    public static readonly IReadOnlyList<string> All = [Development, Architecture, Product, Pmo, Functional, General];

    public static bool IsValid(string? value) => value is not null && All.Contains(value);

    /// <summary>Lower-cases and trims what the caller sent, so "Architecture " is accepted. Blank or null means <see cref="General"/>.</summary>
    public static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? General : value.Trim().ToLowerInvariant();
}
