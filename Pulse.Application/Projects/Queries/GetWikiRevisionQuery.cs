using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Projects.Queries;

public record GetWikiRevisionQuery(Guid ProjectId, Guid PageId, Guid RevisionId, Guid ActorId, string ActorRole) : IRequest<ServiceResult<WikiPageRevisionContentDto>>;

public class GetWikiRevisionHandler : IRequestHandler<GetWikiRevisionQuery, ServiceResult<WikiPageRevisionContentDto>>
{
    private readonly IWikiRepository _wiki;
    private readonly IProjectAccessPolicy _access;

    public GetWikiRevisionHandler(IWikiRepository wiki, IProjectAccessPolicy access)
    {
        _wiki = wiki;
        _access = access;
    }

    public async Task<ServiceResult<WikiPageRevisionContentDto>> Handle(GetWikiRevisionQuery query, CancellationToken ct)
    {
        var page = await _wiki.GetByIdAsync(query.PageId, ct);
        if (page is null || page.ProjectId != query.ProjectId)
            return ServiceResult<WikiPageRevisionContentDto>.Fail("NOT_FOUND", "Wiki page not found.");

        if (!Roles.IsOrgReadOnlyViewer(query.ActorRole) && !await _access.CanAccessProjectAsync(page.ProjectId, query.ActorId, query.ActorRole, ct))
            return ServiceResult<WikiPageRevisionContentDto>.Fail("FORBIDDEN", "You do not have access to this project.");

        var revision = await _wiki.GetRevisionByIdAsync(query.RevisionId, ct);
        if (revision is null || revision.WikiPageId != query.PageId)
            return ServiceResult<WikiPageRevisionContentDto>.Fail("NOT_FOUND", "Revision not found.");

        return ServiceResult<WikiPageRevisionContentDto>.Ok(WikiPageRevisionContentDto.From(revision));
    }
}
