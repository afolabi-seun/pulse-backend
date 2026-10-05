using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Projects.Commands;

public record DeleteWikiPageCommand(Guid ProjectId, Guid PageId, Guid ActorId, string ActorRole = "") : IRequest<ServiceResult<bool>>;

public class DeleteWikiPageHandler : IRequestHandler<DeleteWikiPageCommand, ServiceResult<bool>>
{
    private readonly IWikiRepository _wiki;
    private readonly IProjectAccessPolicy _access;

    public DeleteWikiPageHandler(IWikiRepository wiki, IProjectAccessPolicy access)
    {
        _wiki = wiki;
        _access = access;
    }

    public async Task<ServiceResult<bool>> Handle(DeleteWikiPageCommand cmd, CancellationToken ct)
    {
        var page = await _wiki.GetByIdAsync(cmd.PageId, ct);
        if (page is null || page.ProjectId != cmd.ProjectId)
            return ServiceResult<bool>.Fail("NOT_FOUND", "Wiki page not found.");

        if (!await _access.CanAccessProjectAsync(page.ProjectId, cmd.ActorId, cmd.ActorRole, ct))
            return ServiceResult<bool>.Fail("FORBIDDEN", "You do not have access to this project.");

        _wiki.Remove(page);
        await _wiki.SaveChangesAsync(ct);

        return ServiceResult<bool>.Ok(true);
    }
}
