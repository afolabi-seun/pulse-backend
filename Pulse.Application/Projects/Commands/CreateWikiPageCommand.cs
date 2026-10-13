using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Projects;
using MediatR;

namespace Pulse.Application.Projects.Commands;

public record CreateWikiPageCommand(
    Guid ProjectId,
    string Title,
    string Content,
    Guid ActorId,
    string ActorRole = "",
    bool RestrictedToMembers = false,
    string? Category = null) : IRequest<ServiceResult<WikiPageDto>>;

public class CreateWikiPageHandler : IRequestHandler<CreateWikiPageCommand, ServiceResult<WikiPageDto>>
{
    private readonly IWikiRepository _wiki;
    private readonly IProjectRepository _projects;
    private readonly IProjectAccessPolicy _access;

    public CreateWikiPageHandler(IWikiRepository wiki, IProjectRepository projects, IProjectAccessPolicy access)
    {
        _wiki = wiki;
        _projects = projects;
        _access = access;
    }

    public async Task<ServiceResult<WikiPageDto>> Handle(CreateWikiPageCommand cmd, CancellationToken ct)
    {
        // A page with no type given is a General one; a type that is not on the list is refused, not stored.
        var category = WikiCategory.Normalize(cmd.Category);
        if (!WikiCategory.IsValid(category))
            return ServiceResult<WikiPageDto>.Fail("VALIDATION_ERROR", $"Unknown page type '{cmd.Category}'. Choose one of: {string.Join(", ", WikiCategory.All)}.");

        var project = await _projects.GetByIdAsync(cmd.ProjectId, ct);
        if (project is null)
            return ServiceResult<WikiPageDto>.Fail("NOT_FOUND", "Project not found.");

        if (!await _access.CanAccessProjectAsync(project.Id, cmd.ActorId, cmd.ActorRole, ct))
            return ServiceResult<WikiPageDto>.Fail("FORBIDDEN", "You do not have access to this project.");

        var page = WikiPage.Create(cmd.ProjectId, cmd.Title, cmd.Content, cmd.ActorId, cmd.RestrictedToMembers, category);
        await _wiki.AddAsync(page, ct);
        await _wiki.SaveChangesAsync(ct);

        return ServiceResult<WikiPageDto>.Ok(WikiPageDto.From(page));
    }
}
