using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Projects.Queries;

public record ListWikiRevisionsQuery(Guid ProjectId, Guid PageId, Guid ActorId, string ActorRole) : IRequest<ServiceResult<IReadOnlyList<WikiPageRevisionDto>>>;

public class ListWikiRevisionsHandler : IRequestHandler<ListWikiRevisionsQuery, ServiceResult<IReadOnlyList<WikiPageRevisionDto>>>
{
    private readonly IWikiRepository _wiki;
    private readonly IProjectAccessPolicy _access;

    public ListWikiRevisionsHandler(IWikiRepository wiki, IProjectAccessPolicy access)
    {
        _wiki = wiki;
        _access = access;
    }

    public async Task<ServiceResult<IReadOnlyList<WikiPageRevisionDto>>> Handle(ListWikiRevisionsQuery query, CancellationToken ct)
    {
        var page = await _wiki.GetByIdAsync(query.PageId, ct);
        if (page is null || page.ProjectId != query.ProjectId)
            return ServiceResult<IReadOnlyList<WikiPageRevisionDto>>.Fail("NOT_FOUND", "Wiki page not found.");

        if (!Roles.IsOrgReadOnlyViewer(query.ActorRole) && !await _access.CanAccessProjectAsync(page.ProjectId, query.ActorId, query.ActorRole, ct))
            return ServiceResult<IReadOnlyList<WikiPageRevisionDto>>.Fail("FORBIDDEN", "You do not have access to this project.");

        var revisions = await _wiki.GetRevisionsAsync(query.PageId, ct);
        return ServiceResult<IReadOnlyList<WikiPageRevisionDto>>.Ok(
            revisions.Select(WikiPageRevisionDto.From).ToList());
    }
}
