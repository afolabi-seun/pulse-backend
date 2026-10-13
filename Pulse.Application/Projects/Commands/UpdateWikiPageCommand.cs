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
    bool? RestrictedToMembers = null,
    string? Category = null) : IRequest<ServiceResult<WikiPageDto>>;

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
        // Null (or blank) leaves the page's type as it is; a value that is not on the list is refused.
        if (!string.IsNullOrWhiteSpace(cmd.Category) && !WikiCategory.IsValid(WikiCategory.Normalize(cmd.Category)))
            return ServiceResult<WikiPageDto>.Fail("VALIDATION_ERROR", $"Unknown page type '{cmd.Category}'. Choose one of: {string.Join(", ", WikiCategory.All)}.");

        var page = await _wiki.GetByIdAsync(cmd.PageId, ct);
        if (page is null || page.ProjectId != cmd.ProjectId)
            return ServiceResult<WikiPageDto>.Fail("NOT_FOUND", "Wiki page not found.");

        if (!await _access.CanAccessProjectAsync(page.ProjectId, cmd.ActorId, cmd.ActorRole, ct))
            return ServiceResult<WikiPageDto>.Fail("FORBIDDEN", "You do not have access to this project.");

        var revision = WikiPageRevision.Create(page.Id, page.Title, page.Content, cmd.ActorId);
        await _wiki.AddRevisionAsync(revision, ct);

        page.Update(cmd.Title, cmd.Content, cmd.RestrictedToMembers, cmd.Category);
        await _wiki.SaveChangesAsync(ct);

        return ServiceResult<WikiPageDto>.Ok(WikiPageDto.From(page));
    }
}
