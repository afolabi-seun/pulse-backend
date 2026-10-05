using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Projects.Queries;

public record ListAllWikiPagesQuery(Guid ActorId, string ActorRole, int Limit = 25, string? Cursor = null)
    : IRequest<ServiceResult<PagedResultDto<WikiIndexEntryDto>>>;

public class ListAllWikiPagesHandler : IRequestHandler<ListAllWikiPagesQuery, ServiceResult<PagedResultDto<WikiIndexEntryDto>>>
{
    private readonly IWikiRepository _wiki;
    private readonly IProjectAccessPolicy _access;

    public ListAllWikiPagesHandler(IWikiRepository wiki, IProjectAccessPolicy access)
    {
        _wiki = wiki;
        _access = access;
    }

    public async Task<ServiceResult<PagedResultDto<WikiIndexEntryDto>>> Handle(ListAllWikiPagesQuery query, CancellationToken ct)
    {
        var limit  = Math.Clamp(query.Limit, 1, 100);
        var offset = query.Cursor is not null
            ? int.Parse(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(query.Cursor)))
            : 0;

        // hasMore/nextCursor advance over the underlying rows, not the post-filter visible count —
        // a page restricted to members a caller can't see just makes this page's visible count
        // a little short of `limit`, exactly as the old full-fetch-then-filter version already did.
        var raw     = await _wiki.ListAllPagedAsync(limit + 1, query.Cursor, ct);
        var hasMore = raw.Count > limit;
        var page    = hasMore ? raw.Take(limit).ToList() : raw.ToList();

        var accessByProject = new Dictionary<Guid, bool>();
        var visible = new List<WikiIndexEntryDto>();
        foreach (var entry in page)
        {
            if (!entry.RestrictedToMembers)
            {
                visible.Add(entry);
                continue;
            }
            if (!accessByProject.TryGetValue(entry.ProjectId, out var canAccess))
            {
                canAccess = Roles.IsOrgReadOnlyViewer(query.ActorRole)
                    || await _access.CanAccessProjectAsync(entry.ProjectId, query.ActorId, query.ActorRole, ct);
                accessByProject[entry.ProjectId] = canAccess;
            }
            if (canAccess)
                visible.Add(entry);
        }

        var nextCursor = hasMore
            ? Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes((offset + limit).ToString()))
            : null;

        return ServiceResult<PagedResultDto<WikiIndexEntryDto>>.Ok(
            new PagedResultDto<WikiIndexEntryDto>(visible, nextCursor, hasMore));
    }
}
