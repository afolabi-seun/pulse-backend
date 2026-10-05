using Pulse.Application.Projects;
using Pulse.Domain.Projects;

namespace Pulse.Application.Common.Interfaces;

public interface IWikiRepository
{
    Task<WikiPage?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<WikiPage>> ListByProjectPagedAsync(Guid projectId, int limit, string? cursor, CancellationToken ct = default);
    Task<IReadOnlyList<WikiIndexEntryDto>> ListAllPagedAsync(int limit, string? cursor, CancellationToken ct = default);
    Task AddAsync(WikiPage page, CancellationToken ct = default);
    void Remove(WikiPage page);
    Task SaveChangesAsync(CancellationToken ct = default);

    // Revisions
    Task<IReadOnlyList<WikiPageRevision>> GetRevisionsAsync(Guid wikiPageId, CancellationToken ct = default);
    Task<WikiPageRevision?> GetRevisionByIdAsync(Guid revisionId, CancellationToken ct = default);
    Task AddRevisionAsync(WikiPageRevision revision, CancellationToken ct = default);
}
