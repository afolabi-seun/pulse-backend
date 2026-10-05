using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Tasks;
using MediatR;

namespace Pulse.Application.Tasks.Commands;

public record TaskDependencyDto(Guid Id, Guid BlockingTaskId, Guid DependentTaskId);

public record AddTaskDependencyCommand(
    Guid BlockingTaskId,
    Guid DependentTaskId,
    Guid ActorId,
    string? IpAddress,
    string ActorRole = "") : IRequest<ServiceResult<TaskDependencyDto>>;

public class AddTaskDependencyHandler : IRequestHandler<AddTaskDependencyCommand, ServiceResult<TaskDependencyDto>>
{
    private readonly ITaskRepository _tasks;
    private readonly ITaskDependencyRepository _deps;
    private readonly IAuditLogRepository _audit;
    private readonly IProjectAccessPolicy _access;

    public AddTaskDependencyHandler(ITaskRepository tasks, ITaskDependencyRepository deps, IAuditLogRepository audit, IProjectAccessPolicy access)
    {
        _tasks = tasks;
        _deps = deps;
        _audit = audit;
        _access = access;
    }

    public async Task<ServiceResult<TaskDependencyDto>> Handle(AddTaskDependencyCommand cmd, CancellationToken ct)
    {
        if (cmd.BlockingTaskId == cmd.DependentTaskId)
            return ServiceResult<TaskDependencyDto>.Fail("BUSINESS_RULE_VIOLATION", "A task cannot depend on itself.");

        var blocking = await _tasks.GetByIdAsync(cmd.BlockingTaskId, ct);
        if (blocking is null)
            return ServiceResult<TaskDependencyDto>.Fail("NOT_FOUND", $"Blocking task '{cmd.BlockingTaskId}' not found.");

        var dependent = await _tasks.GetByIdAsync(cmd.DependentTaskId, ct);
        if (dependent is null)
            return ServiceResult<TaskDependencyDto>.Fail("NOT_FOUND", $"Dependent task '{cmd.DependentTaskId}' not found.");

        // Must be able to access both ends of the link — being the assignee of either task is enough
        // on its own, mirroring Pause/Resume's access rule.
        var canAccessDependent = dependent.AssigneeId == cmd.ActorId
            || await _access.CanAccessProjectAsync(dependent.ProjectId, cmd.ActorId, cmd.ActorRole, ct);
        var canAccessBlocking = blocking.AssigneeId == cmd.ActorId
            || await _access.CanAccessProjectAsync(blocking.ProjectId, cmd.ActorId, cmd.ActorRole, ct);
        if (!canAccessDependent || !canAccessBlocking)
            return ServiceResult<TaskDependencyDto>.Fail("FORBIDDEN", "You do not have access to one or both of these tasks.");

        if (await _deps.ExistsAsync(cmd.BlockingTaskId, cmd.DependentTaskId, ct))
            return ServiceResult<TaskDependencyDto>.Fail("BUSINESS_RULE_VIOLATION", "Dependency already exists.");

        // Prevent circular: check if blocking depends on dependent (simple one-level check)
        if (await _deps.ExistsAsync(cmd.DependentTaskId, cmd.BlockingTaskId, ct))
            return ServiceResult<TaskDependencyDto>.Fail("BUSINESS_RULE_VIOLATION", "Circular dependency detected.");

        var dep = TaskDependency.Create(cmd.BlockingTaskId, cmd.DependentTaskId);
        await _deps.AddAsync(dep, ct);
        await _deps.SaveChangesAsync(ct);

        await _audit.LogAsync("TASK_DEPENDENCY_ADDED", cmd.ActorId, cmd.IpAddress,
            $"Task '{cmd.DependentTaskId}' now blocked by '{cmd.BlockingTaskId}'", ct);

        return ServiceResult<TaskDependencyDto>.Ok(new TaskDependencyDto(dep.Id, dep.BlockingTaskId, dep.DependentTaskId));
    }
}

public record RemoveTaskDependencyCommand(
    Guid BlockingTaskId,
    Guid DependentTaskId,
    Guid ActorId,
    string? IpAddress,
    string ActorRole = "") : IRequest<ServiceResult<bool>>;

public class RemoveTaskDependencyHandler : IRequestHandler<RemoveTaskDependencyCommand, ServiceResult<bool>>
{
    private readonly ITaskDependencyRepository _deps;
    private readonly IAuditLogRepository _audit;
    private readonly ITaskRepository _tasks;
    private readonly IProjectAccessPolicy _access;

    public RemoveTaskDependencyHandler(ITaskDependencyRepository deps, IAuditLogRepository audit, ITaskRepository tasks, IProjectAccessPolicy access)
    {
        _deps = deps;
        _audit = audit;
        _tasks = tasks;
        _access = access;
    }

    public async Task<ServiceResult<bool>> Handle(RemoveTaskDependencyCommand cmd, CancellationToken ct)
    {
        if (!await _deps.ExistsAsync(cmd.BlockingTaskId, cmd.DependentTaskId, ct))
            return ServiceResult<bool>.Fail("NOT_FOUND", "Dependency not found.");

        var dependent = await _tasks.GetByIdAsync(cmd.DependentTaskId, ct);
        if (dependent is not null
            && dependent.AssigneeId != cmd.ActorId
            && !await _access.CanAccessProjectAsync(dependent.ProjectId, cmd.ActorId, cmd.ActorRole, ct))
            return ServiceResult<bool>.Fail("FORBIDDEN", "You do not have access to this task.");

        await _deps.RemoveAsync(cmd.BlockingTaskId, cmd.DependentTaskId, ct);
        await _deps.SaveChangesAsync(ct);

        await _audit.LogAsync("TASK_DEPENDENCY_REMOVED", cmd.ActorId, cmd.IpAddress,
            $"Removed dependency: '{cmd.DependentTaskId}' unblocked from '{cmd.BlockingTaskId}'", ct);

        return ServiceResult<bool>.Ok(true);
    }
}
