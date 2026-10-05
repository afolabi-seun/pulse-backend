using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Common;
using MediatR;
using DomainTaskStatus = Pulse.Domain.Tasks.TaskStatus;

namespace Pulse.Application.Tasks.Commands;

/// <summary>
/// Self-service grooming for an engineer's own self-created task — the narrow gap left by
/// PATCH /tasks/{id} being TeamLeadOrAbove-gated: an Engineer/Designer who creates a task for
/// themselves (auto-assigned, since they can't assign to anyone else — see CreateTaskCommand)
/// has no way to ever add points to it, so it sits in Backlog forever until a Team Lead+ notices
/// and grooms it. This lets the task's own creator set Points/Priority on it themselves, once,
/// to move it out of Backlog — deliberately not a general loosening of task editing (that stays
/// TeamLeadOrAbove via the real PATCH endpoint): only Points and Priority, only while the task is
/// still in Backlog, and only for the engineer who created it.
/// </summary>
public record GroomOwnTaskCommand(
    Guid TaskId,
    int Points,
    int? Priority,
    Guid ActorId,
    string? IpAddress) : IRequest<ServiceResult<TaskDto>>;

public class GroomOwnTaskHandler : IRequestHandler<GroomOwnTaskCommand, ServiceResult<TaskDto>>
{
    private readonly ITaskRepository _tasks;
    private readonly IAuditLogRepository _audit;

    public GroomOwnTaskHandler(ITaskRepository tasks, IAuditLogRepository audit)
    {
        _tasks = tasks;
        _audit = audit;
    }

    public async Task<ServiceResult<TaskDto>> Handle(GroomOwnTaskCommand cmd, CancellationToken ct)
    {
        var task = await _tasks.GetByIdAsync(cmd.TaskId, ct);
        if (task is null)
            return ServiceResult<TaskDto>.Fail("NOT_FOUND", $"Task '{cmd.TaskId}' not found.");

        if (task.CreatedById != cmd.ActorId || task.AssigneeId != cmd.ActorId)
            return ServiceResult<TaskDto>.Fail("FORBIDDEN", "You can only groom a task you created and are assigned to yourself.");

        if (task.Status != DomainTaskStatus.Backlog)
            return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION", "This task has already been groomed.");

        if (cmd.Points < 1)
            return ServiceResult<TaskDto>.Fail("VALIDATION_ERROR", "Points must be at least 1 to activate this task.");

        try
        {
            task.SetPoints(cmd.Points, cmd.ActorId);
            if (cmd.Priority.HasValue)
                task.SetPriority(cmd.Priority.Value);
        }
        catch (DomainException ex)
        {
            return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION", ex.Message);
        }

        await _tasks.SaveChangesAsync(ct);
        await _audit.LogAsync("TASK_SELF_GROOMED", cmd.ActorId, cmd.IpAddress,
            $"Task '{task.Id}' groomed by its own creator (points={cmd.Points})", ct);

        return ServiceResult<TaskDto>.Ok(TaskDto.From(task));
    }
}
