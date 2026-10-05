using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Tasks.Queries;

public record LinkedTaskDto(Guid Id, string Title, string Status, string? TaskKey = null);

public record TaskLinksDto(
    IReadOnlyList<LinkedTaskDto> BlockedBy,
    IReadOnlyList<LinkedTaskDto> Blocks);

public record GetTaskDependenciesQuery(Guid TaskId, Guid ActorId, string ActorRole) : IRequest<ServiceResult<TaskLinksDto>>;

public class GetTaskDependenciesHandler : IRequestHandler<GetTaskDependenciesQuery, ServiceResult<TaskLinksDto>>
{
    private readonly ITaskDependencyRepository _deps;
    private readonly ITaskRepository _tasks;
    private readonly IProjectRepository _projects;
    private readonly IProjectAccessPolicy _access;

    public GetTaskDependenciesHandler(ITaskDependencyRepository deps, ITaskRepository tasks, IProjectRepository projects, IProjectAccessPolicy access)
    {
        _deps  = deps;
        _tasks = tasks;
        _projects = projects;
        _access = access;
    }

    public async Task<ServiceResult<TaskLinksDto>> Handle(GetTaskDependenciesQuery query, CancellationToken ct)
    {
        var root = await _tasks.GetByIdAsync(query.TaskId, ct);
        if (root is null)
            return ServiceResult<TaskLinksDto>.Fail("NOT_FOUND", $"Task '{query.TaskId}' not found.");

        var allowed = Roles.IsOrgReadOnlyViewer(query.ActorRole)
            || await _access.CanViewTaskAsync(query.TaskId, query.ActorId, query.ActorRole, ct);
        if (!allowed)
            return ServiceResult<TaskLinksDto>.Fail("FORBIDDEN", "You do not have access to this task.");

        var blocking  = await _deps.GetBlockingAsync(query.TaskId, ct);
        var dependents = await _deps.GetDependentsAsync(query.TaskId, ct);

        var blockingTasks  = new List<Domain.Tasks.PulseTask>();
        foreach (var d in blocking)
        {
            var t = await _tasks.GetByIdAsync(d.BlockingTaskId, ct);
            if (t is not null) blockingTasks.Add(t);
        }

        var dependentTasks = new List<Domain.Tasks.PulseTask>();
        foreach (var d in dependents)
        {
            var t = await _tasks.GetByIdAsync(d.DependentTaskId, ct);
            if (t is not null) dependentTasks.Add(t);
        }

        var projectIds = blockingTasks.Concat(dependentTasks).Select(t => t.ProjectId).Distinct().ToList();
        var projectCodes = projectIds.Count > 0
            ? await _projects.GetCodesByIdsAsync(projectIds, ct)
            : new Dictionary<Guid, string>();

        LinkedTaskDto ToDto(Domain.Tasks.PulseTask t) => new(
            t.Id, t.Title, t.Status.ToString(),
            projectCodes.TryGetValue(t.ProjectId, out var code) ? $"{code}-{t.TaskNumber}" : null);

        var blockedBy = blockingTasks.Select(ToDto).ToList();
        var blocks = dependentTasks.Select(ToDto).ToList();

        return ServiceResult<TaskLinksDto>.Ok(new TaskLinksDto(blockedBy, blocks));
    }
}
