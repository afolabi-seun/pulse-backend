using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Epics;
using Pulse.Domain.Common;
using MediatR;

namespace Pulse.Application.Projects.Commands;

public record PauseProjectCommand(
    Guid ProjectId,
    Guid ActorId,
    string? IpAddress,
    string ActorRole = "") : IRequest<ServiceResult<ProjectDto>>;

public class PauseProjectHandler : IRequestHandler<PauseProjectCommand, ServiceResult<ProjectDto>>
{
    private readonly IProjectRepository _projects;
    private readonly ITaskRepository _tasks;
    private readonly IEpicRepository _epics;
    private readonly IAuditLogRepository _audit;
    private readonly IProjectAccessPolicy _access;

    public PauseProjectHandler(
        IProjectRepository projects, ITaskRepository tasks, IEpicRepository epics,
        IAuditLogRepository audit, IProjectAccessPolicy access)
    {
        _projects = projects;
        _tasks = tasks;
        _epics = epics;
        _audit = audit;
        _access = access;
    }

    public async Task<ServiceResult<ProjectDto>> Handle(PauseProjectCommand cmd, CancellationToken ct)
    {
        var project = await _projects.GetByIdAsync(cmd.ProjectId, ct);
        if (project is null)
            return ServiceResult<ProjectDto>.Fail("NOT_FOUND", $"Project '{cmd.ProjectId}' not found.");

        if (!await _access.CanAccessProjectAsync(project.Id, cmd.ActorId, cmd.ActorRole, ct))
            return ServiceResult<ProjectDto>.Fail("FORBIDDEN", "You do not have access to this project.");

        try
        {
            project.Pause();
        }
        catch (DomainException ex)
        {
            return ServiceResult<ProjectDto>.Fail("BUSINESS_RULE_VIOLATION", ex.Message);
        }

        var tasks = await _tasks.GetByProjectAsync(project.Id, ct);
        var epicIds = new HashSet<Guid>();
        var pausedCount = 0;

        foreach (var task in tasks)
        {
            if (task.Status is not (Domain.Tasks.TaskStatus.Active or Domain.Tasks.TaskStatus.Blocked))
                continue;

            task.Pause(note: null, cmd.ActorId, byProject: true);
            pausedCount++;
            if (task.EpicId.HasValue)
                epicIds.Add(task.EpicId.Value);
        }

        await _tasks.SaveChangesAsync(ct);

        foreach (var epicId in epicIds)
            await EpicStatusRollUp.ApplyAsync(epicId, _tasks, _epics, ct);

        await _audit.LogAsync("PROJECT_PAUSED", cmd.ActorId, cmd.IpAddress,
            $"Paused project '{project.Id}' '{project.Name}' ({pausedCount} task(s) paused).", ct);

        return ServiceResult<ProjectDto>.Ok(ProjectDto.From(project));
    }
}
