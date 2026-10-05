using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Projects.Queries;

public record GetProjectQuery(Guid ProjectId, Guid ActorId, string ActorRole) : IRequest<ServiceResult<ProjectDto>>;

public class GetProjectHandler : IRequestHandler<GetProjectQuery, ServiceResult<ProjectDto>>
{
    private readonly IProjectRepository _projects;
    private readonly IProjectAccessPolicy _access;

    public GetProjectHandler(IProjectRepository projects, IProjectAccessPolicy access)
    {
        _projects = projects;
        _access = access;
    }

    public async Task<ServiceResult<ProjectDto>> Handle(GetProjectQuery query, CancellationToken ct)
    {
        var project = await _projects.GetByIdAsync(query.ProjectId, ct);
        if (project is null)
            return ServiceResult<ProjectDto>.Fail("NOT_FOUND", $"Project '{query.ProjectId}' not found.");

        // Executive, HR, and Accountant are read-only and org-wide, but deliberately excluded from
        // the shared ProjectAccessPolicy's global roles — that policy also authorizes writes
        // (task/project create, update, delete), and none of the three must ever pass those.
        // Short-circuit here instead, in this read-only query only.
        var allowed = Roles.IsOrgReadOnlyViewer(query.ActorRole)
            || await _access.CanAccessProjectAsync(project.Id, query.ActorId, query.ActorRole, ct);
        if (!allowed)
            return ServiceResult<ProjectDto>.Fail("FORBIDDEN", "You do not have access to this project.");

        return ServiceResult<ProjectDto>.Ok(ProjectDto.From(project));
    }
}
