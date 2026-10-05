using Pulse.Domain.Projects;

namespace Pulse.Application.Projects;

public record WikiPageSummaryDto(Guid Id, string Title, DateTime CreatedAt, DateTime? UpdatedAt, bool RestrictedToMembers = false)
{
    public static WikiPageSummaryDto From(WikiPage p) => new(p.Id, p.Title, p.CreatedAt, p.UpdatedAt, p.RestrictedToMembers);
}

public record WikiPageDto(Guid Id, Guid ProjectId, string Title, string Content, Guid AuthorId, DateTime CreatedAt, DateTime? UpdatedAt, bool RestrictedToMembers = false)
{
    public static WikiPageDto From(WikiPage p) =>
        new(p.Id, p.ProjectId, p.Title, p.Content, p.AuthorId, p.CreatedAt, p.UpdatedAt, p.RestrictedToMembers);
}

public record WikiPageRevisionDto(Guid Id, string Title, DateTime CreatedAt)
{
    public static WikiPageRevisionDto From(WikiPageRevision r) => new(r.Id, r.Title, r.CreatedAt);
}

public record WikiPageRevisionContentDto(Guid Id, string Title, string Content, DateTime CreatedAt)
{
    public static WikiPageRevisionContentDto From(WikiPageRevision r) =>
        new(r.Id, r.Title, r.Content, r.CreatedAt);
}

public record WikiIndexEntryDto(Guid PageId, string PageTitle, Guid ProjectId, string ProjectName, DateTime CreatedAt, DateTime? UpdatedAt, bool RestrictedToMembers = false);
