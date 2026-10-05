using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Projects.Queries;

public record ListWikiPagesQuery(Guid ProjectId, Guid ActorId, string ActorRole, int Limit = 25, string? Cursor = null)
    : IRequest<ServiceResult<PagedResultDto<WikiPageSummaryDto>>>;

public class ListWikiPagesHandler : IRequestHandler<ListWikiPagesQuery, ServiceResult<PagedResultDto<WikiPageSummaryDto>>>
{
    private readonly IWikiRepository _wiki;
    private readonly IProjectRepository _projects;
    private readonly IProjectAccessPolicy _access;

    public ListWikiPagesHandler(IWikiRepository wiki, IProjectRepository projects, IProjectAccessPolicy access)
    {
        _wiki = wiki;
        _projects = projects;
        _access = access;
    }

    public async Task<ServiceResult<PagedResultDto<WikiPageSummaryDto>>> Handle(ListWikiPagesQuery query, CancellationToken ct)
    {
        var project = await _projects.GetByIdAsync(query.ProjectId, ct);
        if (project is null)
            return ServiceResult<PagedResultDto<WikiPageSummaryDto>>.Fail("NOT_FOUND", "Project not found.");

        if (!Roles.IsOrgReadOnlyViewer(query.ActorRole) && !await _access.CanAccessProjectAsync(project.Id, query.ActorId, query.ActorRole, ct))
            return ServiceResult<PagedResultDto<WikiPageSummaryDto>>.Fail("FORBIDDEN", "You do not have access to this project.");

        var limit  = Math.Clamp(query.Limit, 1, 100);
        var offset = query.Cursor is not null
            ? int.Parse(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(query.Cursor)))
            : 0;

        var raw     = await _wiki.ListByProjectPagedAsync(query.ProjectId, limit + 1, query.Cursor, ct);
        var hasMore = raw.Count > limit;
        var page    = hasMore ? raw.Take(limit).ToList() : raw.ToList();

        var nextCursor = hasMore
            ? Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes((offset + limit).ToString()))
            : null;

        return ServiceResult<PagedResultDto<WikiPageSummaryDto>>.Ok(
            new PagedResultDto<WikiPageSummaryDto>(page.Select(WikiPageSummaryDto.From).ToList(), nextCursor, hasMore));
    }
}
