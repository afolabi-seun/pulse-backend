using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Projects;
using MediatR;

namespace Pulse.Application.Projects.Commands;

public record UpdateWikiPageCommand(
    Guid ProjectId,
    Guid PageId,
    string Title,
    string Content,
    Guid ActorId,
    string ActorRole = "",
    bool? RestrictedToMembers = null) : IRequest<ServiceResult<WikiPageDto>>;

public class UpdateWikiPageHandler : IRequestHandler<UpdateWikiPageCommand, ServiceResult<WikiPageDto>>
{
    private readonly IWikiRepository _wiki;
    private readonly IProjectAccessPolicy _access;

    public UpdateWikiPageHandler(IWikiRepository wiki, IProjectAccessPolicy access)
    {
        _wiki = wiki;
        _access = access;
    }

    public async Task<ServiceResult<WikiPageDto>> Handle(UpdateWikiPageCommand cmd, CancellationToken ct)
    {
        var page = await _wiki.GetByIdAsync(cmd.PageId, ct);
        if (page is null || page.ProjectId != cmd.ProjectId)
            return ServiceResult<WikiPageDto>.Fail("NOT_FOUND", "Wiki page not found.");

        if (!await _access.CanAccessProjectAsync(page.ProjectId, cmd.ActorId, cmd.ActorRole, ct))
            return ServiceResult<WikiPageDto>.Fail("FORBIDDEN", "You do not have access to this project.");

        var revision = WikiPageRevision.Create(page.Id, page.Title, page.Content, cmd.ActorId);
        await _wiki.AddRevisionAsync(revision, ct);

        page.Update(cmd.Title, cmd.Content, cmd.RestrictedToMembers);
        await _wiki.SaveChangesAsync(ct);

        return ServiceResult<WikiPageDto>.Ok(WikiPageDto.From(page));
    }
}
