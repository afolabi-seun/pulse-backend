using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Projects.Commands;

public record RemoveProjectMemberCommand(Guid ProjectId, Guid EngineerId, Guid ActorId, string ActorRole = "") : IRequest<ServiceResult<IReadOnlyList<ProjectMemberDto>>>;

public class RemoveProjectMemberHandler : IRequestHandler<RemoveProjectMemberCommand, ServiceResult<IReadOnlyList<ProjectMemberDto>>>
{
    private readonly IProjectRepository _projects;
    private readonly IProjectAccessPolicy _access;

    public RemoveProjectMemberHandler(IProjectRepository projects, IProjectAccessPolicy access)
    {
        _projects = projects;
        _access = access;
    }

    public async Task<ServiceResult<IReadOnlyList<ProjectMemberDto>>> Handle(RemoveProjectMemberCommand command, CancellationToken ct)
    {
        var project = await _projects.GetByIdAsync(command.ProjectId, ct);
        if (project is null)
            return ServiceResult<IReadOnlyList<ProjectMemberDto>>.Fail("NOT_FOUND", "Project not found.");

        if (!await _access.CanAccessProjectAsync(project.Id, command.ActorId, command.ActorRole, ct))
            return ServiceResult<IReadOnlyList<ProjectMemberDto>>.Fail("FORBIDDEN", "You do not have access to this project.");

        await _projects.RemoveMemberAsync(command.ProjectId, command.EngineerId, ct);
        var members = await _projects.ListMembersAsync(command.ProjectId, ct);
        return ServiceResult<IReadOnlyList<ProjectMemberDto>>.Ok(members);
    }
}
