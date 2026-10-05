using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Epics;
using Pulse.Domain.Common;
using MediatR;

namespace Pulse.Application.Projects.Commands;

public record ResumeProjectCommand(
    Guid ProjectId,
    Guid ActorId,
    string? IpAddress,
    string ActorRole = "") : IRequest<ServiceResult<ProjectDto>>;

public class ResumeProjectHandler : IRequestHandler<ResumeProjectCommand, ServiceResult<ProjectDto>>
{
    private readonly IProjectRepository _projects;
    private readonly ITaskRepository _tasks;
    private readonly IEpicRepository _epics;
    private readonly IEscalationEventRepository _escalationEvents;
    private readonly IAuditLogRepository _audit;
    private readonly IProjectAccessPolicy _access;

    public ResumeProjectHandler(
        IProjectRepository projects, ITaskRepository tasks, IEpicRepository epics,
        IEscalationEventRepository escalationEvents, IAuditLogRepository audit, IProjectAccessPolicy access)
    {
        _projects = projects;
        _tasks = tasks;
        _epics = epics;
        _escalationEvents = escalationEvents;
        _audit = audit;
        _access = access;
    }

    public async Task<ServiceResult<ProjectDto>> Handle(ResumeProjectCommand cmd, CancellationToken ct)
    {
        var project = await _projects.GetByIdAsync(cmd.ProjectId, ct);
        if (project is null)
            return ServiceResult<ProjectDto>.Fail("NOT_FOUND", $"Project '{cmd.ProjectId}' not found.");

        if (!await _access.CanAccessProjectAsync(project.Id, cmd.ActorId, cmd.ActorRole, ct))
            return ServiceResult<ProjectDto>.Fail("FORBIDDEN", "You do not have access to this project.");

        try
        {
            project.Resume();
        }
        catch (DomainException ex)
        {
            return ServiceResult<ProjectDto>.Fail("BUSINESS_RULE_VIOLATION", ex.Message);
        }

        var tasks = await _tasks.GetByProjectAsync(project.Id, ct);
        var epicIds = new HashSet<Guid>();
        var resumedTaskIds = new List<Guid>();

        foreach (var task in tasks)
        {
            if (task.Status != Domain.Tasks.TaskStatus.Paused || !task.PausedByProject)
                continue;

            task.ResumeFromProjectHold(cmd.ActorId);
            resumedTaskIds.Add(task.Id);
            if (task.EpicId.HasValue)
                epicIds.Add(task.EpicId.Value);
        }

        await _tasks.SaveChangesAsync(ct);

        foreach (var epicId in epicIds)
            await EpicStatusRollUp.ApplyAsync(epicId, _tasks, _epics, ct);

        // Due dates may have shifted forward — re-arm so the scanner evaluates against the new date.
        foreach (var taskId in resumedTaskIds)
            await _escalationEvents.ClearForTaskAsync(taskId, ct);

        await _audit.LogAsync("PROJECT_RESUMED", cmd.ActorId, cmd.IpAddress,
            $"Resumed project '{project.Id}' '{project.Name}' ({resumedTaskIds.Count} task(s) resumed).", ct);

        return ServiceResult<ProjectDto>.Ok(ProjectDto.From(project));
    }
}
