using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Projects.Queries;

public record GetWikiPageQuery(Guid ProjectId, Guid PageId, Guid ActorId, string ActorRole) : IRequest<ServiceResult<WikiPageDto>>;

public class GetWikiPageHandler : IRequestHandler<GetWikiPageQuery, ServiceResult<WikiPageDto>>
{
    private readonly IWikiRepository _wiki;
    private readonly IProjectAccessPolicy _access;

    public GetWikiPageHandler(IWikiRepository wiki, IProjectAccessPolicy access)
    {
        _wiki = wiki;
        _access = access;
    }

    public async Task<ServiceResult<WikiPageDto>> Handle(GetWikiPageQuery query, CancellationToken ct)
    {
        var page = await _wiki.GetByIdAsync(query.PageId, ct);
        if (page is null || page.ProjectId != query.ProjectId)
            return ServiceResult<WikiPageDto>.Fail("NOT_FOUND", "Wiki page not found.");

        // Open to every signed-in user unless the author restricted it to the project's members; Executive/HR/
        // Accountant read everything, restricted or not, as they do the rest of a project.
        if (page.RestrictedToMembers
            && !Roles.IsOrgReadOnlyViewer(query.ActorRole)
            && !await _access.CanAccessProjectAsync(page.ProjectId, query.ActorId, query.ActorRole, ct))
            return ServiceResult<WikiPageDto>.Fail("FORBIDDEN", "This page is restricted to the project's members.");

        return ServiceResult<WikiPageDto>.Ok(WikiPageDto.From(page));
    }
}
