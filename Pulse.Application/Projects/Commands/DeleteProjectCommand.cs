using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Projects.Commands;

public record DeleteProjectCommand(Guid ProjectId, Guid ActorId, string? IpAddress, string ActorRole = "") : IRequest<ServiceResult<bool>>;

public class DeleteProjectHandler : IRequestHandler<DeleteProjectCommand, ServiceResult<bool>>
{
    private readonly IProjectRepository _projects;
    private readonly ITaskRepository _tasks;
    private readonly IAuditLogRepository _audit;
    private readonly IProjectAccessPolicy _access;

    public DeleteProjectHandler(IProjectRepository projects, ITaskRepository tasks, IAuditLogRepository audit, IProjectAccessPolicy access)
    {
        _projects = projects;
        _tasks = tasks;
        _audit = audit;
        _access = access;
    }

    public async Task<ServiceResult<bool>> Handle(DeleteProjectCommand cmd, CancellationToken ct)
    {
        var project = await _projects.GetByIdAsync(cmd.ProjectId, ct);
        if (project is null)
            return ServiceResult<bool>.Fail("NOT_FOUND", "Project not found.");

        if (!await _access.CanAccessProjectAsync(project.Id, cmd.ActorId, cmd.ActorRole, ct))
            return ServiceResult<bool>.Fail("FORBIDDEN", "You do not have access to this project.");

        var (tasks, _) = await _tasks.ListAsync(
            cmd.ProjectId, null, null, null, null, null, 1, null, ct: ct);

        if (tasks.Count > 0)
            return ServiceResult<bool>.Fail("BUSINESS_RULE_VIOLATION",
                "Cannot delete a project that has tasks. Archive it instead, or delete all tasks first.");

        _projects.Remove(project);
        await _projects.SaveChangesAsync(ct);
        await _audit.LogAsync("PROJECT_DELETED", cmd.ActorId, cmd.IpAddress,
            $"Deleted project {project.Id} ({project.Name})", ct);

        return ServiceResult<bool>.Ok(true);
    }
}
