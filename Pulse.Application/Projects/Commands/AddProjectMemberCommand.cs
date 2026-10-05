using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Projects.Commands;

public record AddProjectMemberCommand(Guid ProjectId, Guid EngineerId, Guid ActorId, string ActorRole = "") : IRequest<ServiceResult<IReadOnlyList<ProjectMemberDto>>>;

public class AddProjectMemberHandler : IRequestHandler<AddProjectMemberCommand, ServiceResult<IReadOnlyList<ProjectMemberDto>>>
{
    private readonly IProjectRepository  _projects;
    private readonly IEngineerRepository _engineers;
    private readonly IProjectAccessPolicy _access;

    public AddProjectMemberHandler(IProjectRepository projects, IEngineerRepository engineers, IProjectAccessPolicy access)
    {
        _projects  = projects;
        _engineers = engineers;
        _access = access;
    }

    public async Task<ServiceResult<IReadOnlyList<ProjectMemberDto>>> Handle(AddProjectMemberCommand command, CancellationToken ct)
    {
        var project = await _projects.GetByIdAsync(command.ProjectId, ct);
        if (project is null)
            return ServiceResult<IReadOnlyList<ProjectMemberDto>>.Fail("NOT_FOUND", "Project not found.");

        if (!await _access.CanAccessProjectAsync(project.Id, command.ActorId, command.ActorRole, ct))
            return ServiceResult<IReadOnlyList<ProjectMemberDto>>.Fail("FORBIDDEN", "You do not have access to this project.");

        var engineer = await _engineers.GetByIdAsync(command.EngineerId, ct);
        if (engineer is null)
            return ServiceResult<IReadOnlyList<ProjectMemberDto>>.Fail("NOT_FOUND", "Engineer not found.");

        await _projects.AddMemberAsync(command.ProjectId, command.EngineerId, ct);
        var members = await _projects.ListMembersAsync(command.ProjectId, ct);
        return ServiceResult<IReadOnlyList<ProjectMemberDto>>.Ok(members);
    }
}
