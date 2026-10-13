using Pulse.Domain.Common;

namespace Pulse.Domain.Projects;

public class WikiPage : Entity
{
    public Guid ProjectId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string Content { get; private set; } = string.Empty;
    public Guid AuthorId { get; private set; }
    public DateTime? UpdatedAt { get; private set; }

    /// <summary>Wiki pages are readable by every signed-in user by default. An author can close a page to
    /// its project's members (and the org-wide read-only roles) when it holds something not meant for everyone.</summary>
    public bool RestrictedToMembers { get; private set; }

    /// <summary>What kind of page this is (see <see cref="WikiCategory"/>), so the wiki can be grouped and filtered by type.</summary>
    public string Category { get; private set; } = WikiCategory.General;

    private WikiPage() { }

    public static WikiPage Create(Guid projectId, string title, string content, Guid authorId, bool restrictedToMembers = false, string? category = null) =>
        new() { ProjectId = projectId, Title = title, Content = content, AuthorId = authorId, RestrictedToMembers = restrictedToMembers, Category = WikiCategory.Normalize(category) };

    public void Update(string title, string content, bool? restrictedToMembers = null, string? category = null)
    {
        Title = title;
        Content = content;
        if (restrictedToMembers.HasValue) RestrictedToMembers = restrictedToMembers.Value;
        if (!string.IsNullOrWhiteSpace(category)) Category = WikiCategory.Normalize(category);
        UpdatedAt = DateTime.UtcNow;
    }
}
