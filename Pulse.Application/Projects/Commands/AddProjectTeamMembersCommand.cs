using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Projects.Commands;

/// <summary>Adds every active member of a team to a project in one step (idempotent).</summary>
public record AddProjectTeamMembersCommand(Guid ProjectId, Guid TeamId, Guid ActorId, string ActorRole = "")
    : IRequest<ServiceResult<IReadOnlyList<ProjectMemberDto>>>;

public class AddProjectTeamMembersHandler : IRequestHandler<AddProjectTeamMembersCommand, ServiceResult<IReadOnlyList<ProjectMemberDto>>>
{
    private readonly IProjectRepository _projects;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;
    private readonly IProjectAccessPolicy _access;

    public AddProjectTeamMembersHandler(
        IProjectRepository projects,
        IEngineerRepository engineers,
        ITeamRepository teams,
        IProjectAccessPolicy access)
    {
        _projects = projects;
        _engineers = engineers;
        _teams = teams;
        _access = access;
    }

    public async Task<ServiceResult<IReadOnlyList<ProjectMemberDto>>> Handle(AddProjectTeamMembersCommand command, CancellationToken ct)
    {
        var project = await _projects.GetByIdAsync(command.ProjectId, ct);
        if (project is null)
            return ServiceResult<IReadOnlyList<ProjectMemberDto>>.Fail("NOT_FOUND", "Project not found.");

        if (!await _access.CanAccessProjectAsync(project.Id, command.ActorId, command.ActorRole, ct))
            return ServiceResult<IReadOnlyList<ProjectMemberDto>>.Fail("FORBIDDEN", "You do not have access to this project.");

        var team = await _teams.GetByIdAsync(command.TeamId, ct);
        if (team is null)
            return ServiceResult<IReadOnlyList<ProjectMemberDto>>.Fail("NOT_FOUND", "Team not found.");

        var teamEngineers = (await _engineers.ListActiveAsync(ct))
            .Where(e => e.TeamId == command.TeamId)
            .ToList();

        // AddMemberAsync is idempotent, so re-adding existing members is a no-op.
        foreach (var engineer in teamEngineers)
            await _projects.AddMemberAsync(command.ProjectId, engineer.Id, ct);

        var members = await _projects.ListMembersAsync(command.ProjectId, ct);
        return ServiceResult<IReadOnlyList<ProjectMemberDto>>.Ok(members);
    }
}
