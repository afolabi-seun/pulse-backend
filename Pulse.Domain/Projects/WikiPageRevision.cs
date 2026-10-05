using Pulse.Domain.Common;

namespace Pulse.Domain.Projects;

public class WikiPageRevision : Entity
{
    public Guid WikiPageId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string Content { get; private set; } = string.Empty;
    public Guid AuthorId { get; private set; }

    private WikiPageRevision() { }

    public static WikiPageRevision Create(Guid wikiPageId, string title, string content, Guid authorId) =>
        new() { WikiPageId = wikiPageId, Title = title, Content = content, AuthorId = authorId };
}
