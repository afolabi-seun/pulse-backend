using Pulse.Application.CheckIns;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Common;
using MediatR;
using DomainTaskStatus = Pulse.Domain.Tasks.TaskStatus;

namespace Pulse.Application.Tasks.Commands;

public record BulkStatusChangeCommand(
    IReadOnlyList<Guid> TaskIds,
    string TargetStatus,
    Guid ActorId,
    string? IpAddress,
    string ActorRole = "") : IRequest<ServiceResult<int>>;

public class BulkStatusChangeHandler : IRequestHandler<BulkStatusChangeCommand, ServiceResult<int>>
{
    private readonly ITaskRepository _tasks;
    private readonly IAuditLogRepository _auditLog;
    private readonly IProjectAccessPolicy _access;
    private readonly ICheckInRepository _checkIns;

    public BulkStatusChangeHandler(ITaskRepository tasks, IAuditLogRepository auditLog, IProjectAccessPolicy access, ICheckInRepository checkIns)
    {
        _tasks   = tasks;
        _auditLog = auditLog;
        _access = access;
        _checkIns = checkIns;
    }

    public async Task<ServiceResult<int>> Handle(BulkStatusChangeCommand command, CancellationToken ct)
    {
        if (command.TaskIds.Count == 0)
            return ServiceResult<int>.Fail("VALIDATION_ERROR", "At least one task ID is required.");

        if (command.TargetStatus is not ("done" or "active"))
            return ServiceResult<int>.Fail("VALIDATION_ERROR", "Target status must be 'done' or 'active'.");

        var changed = 0;
        foreach (var taskId in command.TaskIds.Distinct())
        {
            var task = await _tasks.GetByIdAsync(taskId, ct);
            if (task is null) continue;

            // Silently skip tasks the caller can't access (consistent with skipping
            // non-existent / untransitionable tasks above).
            if (!await _access.CanAccessProjectAsync(task.ProjectId, command.ActorId, command.ActorRole, ct))
                continue;

            try
            {
                if (command.TargetStatus == "done" && task.Status != DomainTaskStatus.Done)
                {
                    task.MarkDone(command.ActorId);
                    await AutoCheckIn.EnsureForTaskCompletionAsync(_checkIns, command.ActorId, task.ProjectId, task.Title, ct);
                    await _auditLog.LogAsync("TASK_BULK_DONE", command.ActorId, command.IpAddress,
                        $"Task '{taskId}' bulk-marked done", ct);
                    changed++;

                    // QA task bulk-accepted — also complete the original (parent) task, same
                    // cascade UpdateTaskCommand already does for the single-task edit path. Without
                    // this, bulk-closing a QA task orphans its parent in InQa forever.
                    if (task.ParentTaskId.HasValue)
                    {
                        var parentTask = await _tasks.GetByIdAsync(task.ParentTaskId.Value, ct);
                        if (parentTask?.Status == DomainTaskStatus.InQa)
                        {
                            parentTask.AcceptQa(command.ActorId);
                            await _auditLog.LogAsync("QA_ACCEPTED", command.ActorId, command.IpAddress,
                                $"QA accepted for task '{parentTask.Id}' via bulk-closed QA task '{taskId}'.", ct);
                        }
                    }
                }
                else if (command.TargetStatus == "active" && task.Status == DomainTaskStatus.Blocked)
                {
                    task.ClearBlocker(command.ActorId);
                    await _auditLog.LogAsync("TASK_BULK_UNBLOCKED", command.ActorId, command.IpAddress,
                        $"Task '{taskId}' bulk-unblocked", ct);
                    changed++;
                }
            }
            catch (DomainException)
            {
                // Skip tasks that can't transition (already done, etc.)
            }
        }

        if (changed > 0)
            await _tasks.SaveChangesAsync(ct);

        return ServiceResult<int>.Ok(changed);
    }
}
