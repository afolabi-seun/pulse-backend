using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Common;
using MediatR;

namespace Pulse.Application.Tasks.Commands;

/// <summary>For a task that is In QA but whose QA task no longer exists: returns it to Active and drops the dead link, so it can be sent to QA
/// again (which creates a fresh QA task, with the usual reviewer pick). Refuses when the QA task does exist — then QA, not this, decides.</summary>
public record RecoverMissingQaTaskCommand(Guid TaskId, Guid ActorId, string? IpAddress, string ActorRole = "") : IRequest<ServiceResult<TaskDto>>;

public class RecoverMissingQaTaskHandler : IRequestHandler<RecoverMissingQaTaskCommand, ServiceResult<TaskDto>>
{
    private readonly ITaskRepository _tasks;
    private readonly IAuditLogRepository _audit;
    private readonly IProjectAccessPolicy _access;

    public RecoverMissingQaTaskHandler(ITaskRepository tasks, IAuditLogRepository audit, IProjectAccessPolicy access)
    {
        _tasks = tasks;
        _audit = audit;
        _access = access;
    }

    public async Task<ServiceResult<TaskDto>> Handle(RecoverMissingQaTaskCommand cmd, CancellationToken ct)
    {
        var task = await _tasks.GetByIdAsync(cmd.TaskId, ct);
        if (task is null)
            return ServiceResult<TaskDto>.Fail("NOT_FOUND", $"Task '{cmd.TaskId}' not found.");

        if (task.AssigneeId != cmd.ActorId
            && !await _access.CanAccessProjectAsync(task.ProjectId, cmd.ActorId, cmd.ActorRole, ct))
            return ServiceResult<TaskDto>.Fail("FORBIDDEN", "You do not have access to this task.");

        if (task.Status != Domain.Tasks.TaskStatus.InQa)
            return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION", "Task is not currently in QA.");

        // Anyone who can open the task can open its QA task (same project), so a missing row here is genuinely gone, not merely hidden from this caller.
        if (task.QaTaskId.HasValue && await _tasks.GetByIdAsync(task.QaTaskId.Value, ct) is not null)
            return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION", "This task's QA task still exists, so it is waiting on QA's decision.");

        var previousQaTaskId = task.QaTaskId;
        try
        {
            task.ReturnFromQaWithoutQaTask(cmd.ActorId, "Its QA task no longer existed");
        }
        catch (DomainException ex)
        {
            return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION", ex.Message);
        }

        await _tasks.SaveChangesAsync(ct);

        await _audit.LogAsync("TASK_QA_RECOVERED", cmd.ActorId, cmd.IpAddress,
            $"Task '{task.Id}' was in QA but its QA task {(previousQaTaskId.HasValue ? $"'{previousQaTaskId}' " : "")}no longer existed; returned to Active.", ct);

        return ServiceResult<TaskDto>.Ok(TaskDto.From(task));
    }
}
