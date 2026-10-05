using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Projects.Queries;

public record ListProjectMembersQuery(Guid ProjectId, Guid ActorId, string ActorRole) : IRequest<ServiceResult<IReadOnlyList<ProjectMemberDto>>>;

public class ListProjectMembersHandler : IRequestHandler<ListProjectMembersQuery, ServiceResult<IReadOnlyList<ProjectMemberDto>>>
{
    private readonly IProjectRepository _projects;
    private readonly IProjectAccessPolicy _access;

    public ListProjectMembersHandler(IProjectRepository projects, IProjectAccessPolicy access)
    {
        _projects = projects;
        _access = access;
    }

    public async Task<ServiceResult<IReadOnlyList<ProjectMemberDto>>> Handle(ListProjectMembersQuery query, CancellationToken ct)
    {
        var project = await _projects.GetByIdAsync(query.ProjectId, ct);
        if (project is null)
            return ServiceResult<IReadOnlyList<ProjectMemberDto>>.Fail("NOT_FOUND", "Project not found.");

        if (!Roles.IsOrgReadOnlyViewer(query.ActorRole) && !await _access.CanAccessProjectAsync(project.Id, query.ActorId, query.ActorRole, ct))
            return ServiceResult<IReadOnlyList<ProjectMemberDto>>.Fail("FORBIDDEN", "You do not have access to this project.");

        var members = await _projects.ListMembersAsync(query.ProjectId, ct);
        return ServiceResult<IReadOnlyList<ProjectMemberDto>>.Ok(members);
    }
}
