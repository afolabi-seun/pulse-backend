using System.Text;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Projects;
using Pulse.Domain.Projects;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.Persistence.Repositories;

public class WikiPageRepository : IWikiRepository
{
    private readonly PulseDbContext _db;

    public WikiPageRepository(PulseDbContext db) => _db = db;

    public async Task<WikiPage?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _db.WikiPages.FirstOrDefaultAsync(p => p.Id == id, ct);

    private static int DecodeOffset(string? cursor) =>
        cursor is null ? 0 : int.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(cursor)));

    public async Task<IReadOnlyList<WikiPage>> ListByProjectPagedAsync(Guid projectId, int limit, string? cursor, CancellationToken ct = default) =>
        await _db.WikiPages
            .Where(p => p.ProjectId == projectId)
            .OrderBy(p => p.Title).ThenBy(p => p.Id)
            .Skip(DecodeOffset(cursor)).Take(limit)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<WikiIndexEntryDto>> ListAllPagedAsync(int limit, string? cursor, CancellationToken ct = default)
    {
        var rows = await _db.WikiPages
            .Join(_db.Projects.Where(p => p.PersonalOwnerId == null), w => w.ProjectId, p => p.Id,
                (w, p) => new { w.Id, w.Title, ProjectId = p.Id, ProjectName = p.Name, w.CreatedAt, w.UpdatedAt, w.RestrictedToMembers })
            .OrderBy(r => r.ProjectName)
            .ThenBy(r => r.Title)
            .ThenBy(r => r.Id)
            .Skip(DecodeOffset(cursor)).Take(limit)
            .ToListAsync(ct);

        return rows
            .Select(r => new WikiIndexEntryDto(r.Id, r.Title, r.ProjectId, r.ProjectName, r.CreatedAt, r.UpdatedAt, r.RestrictedToMembers))
            .ToList();
    }

    public async Task AddAsync(WikiPage page, CancellationToken ct = default) =>
        await _db.WikiPages.AddAsync(page, ct);

    public void Remove(WikiPage page) => _db.WikiPages.Remove(page);

    public async Task SaveChangesAsync(CancellationToken ct = default) =>
        await _db.SaveChangesAsync(ct);

    public async Task<IReadOnlyList<WikiPageRevision>> GetRevisionsAsync(Guid wikiPageId, CancellationToken ct = default) =>
        await _db.WikiPageRevisions
            .Where(r => r.WikiPageId == wikiPageId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct);

    public async Task<WikiPageRevision?> GetRevisionByIdAsync(Guid revisionId, CancellationToken ct = default) =>
        await _db.WikiPageRevisions.FirstOrDefaultAsync(r => r.Id == revisionId, ct);

    public async Task AddRevisionAsync(WikiPageRevision revision, CancellationToken ct = default) =>
        await _db.WikiPageRevisions.AddAsync(revision, ct);
}
