using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Common;
using MediatR;

namespace Pulse.Application.Tasks.Archive;

/// <summary>Brings an archived task back, together with its QA task (or its parent, when it is the QA task), so a task is never restored without the
/// review it is waiting on.</summary>
public record RestoreTaskCommand(Guid TaskId, Guid ActorId, string? IpAddress, string ActorRole = "") : IRequest<ServiceResult<int>>;

public class RestoreTaskHandler : IRequestHandler<RestoreTaskCommand, ServiceResult<int>>
{
    private readonly ITaskRepository _tasks;
    private readonly IAuditLogRepository _audit;

    public RestoreTaskHandler(ITaskRepository tasks, IAuditLogRepository audit)
    {
        _tasks = tasks;
        _audit = audit;
    }

    public async Task<ServiceResult<int>> Handle(RestoreTaskCommand cmd, CancellationToken ct)
    {
        var task = await _tasks.GetByIdIncludingArchivedAsync(cmd.TaskId, ct);
        if (task is null)
            return ServiceResult<int>.Fail("NOT_FOUND", $"Task '{cmd.TaskId}' not found.");
        if (!task.IsArchived)
            return ServiceResult<int>.Fail("BUSINESS_RULE_VIOLATION", "Task is not archived.");

        var rootId = task.ParentTaskId ?? task.Id;
        var group = await _tasks.GetGroupIncludingArchivedAsync(rootId, ct);

        var restored = 0;
        foreach (var t in group.Where(t => t.IsArchived))
        {
            try { t.Restore(cmd.ActorId); restored++; }
            catch (DomainException) { /* restored meanwhile */ }
        }
        await _tasks.SaveChangesAsync(ct);

        await _audit.LogAsync("TASK_RESTORED", cmd.ActorId, cmd.IpAddress,
            $"Restored {restored} archived task(s): '{task.Title}' ({cmd.TaskId})", ct);

        return ServiceResult<int>.Ok(restored);
    }
}
