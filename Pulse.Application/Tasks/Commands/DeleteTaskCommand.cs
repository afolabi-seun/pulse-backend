using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Tasks.Commands;

public record DeleteTaskCommand(Guid TaskId, Guid ActorId, string? IpAddress, string ActorRole = "") : IRequest<ServiceResult<bool>>;

public class DeleteTaskHandler : IRequestHandler<DeleteTaskCommand, ServiceResult<bool>>
{
    private readonly ITaskRepository _tasks;
    private readonly IAuditLogRepository _audit;
    private readonly IProjectAccessPolicy _access;

    public DeleteTaskHandler(ITaskRepository tasks, IAuditLogRepository audit, IProjectAccessPolicy access)
    {
        _tasks = tasks;
        _audit = audit;
        _access = access;
    }

    public async Task<ServiceResult<bool>> Handle(DeleteTaskCommand cmd, CancellationToken ct)
    {
        var task = await _tasks.GetByIdAsync(cmd.TaskId, ct);
        if (task is null)
            return ServiceResult<bool>.Fail("NOT_FOUND", $"Task '{cmd.TaskId}' not found.");

        if (!await _access.CanAccessProjectAsync(task.ProjectId, cmd.ActorId, cmd.ActorRole, ct))
            return ServiceResult<bool>.Fail("FORBIDDEN", "You do not have access to this task.");

        var notes = new List<string>();

        // A QA task is the other half of its parent's In QA state: while it exists the parent waits on it, and when it is gone nothing can accept
        // or reject the parent, so the parent is stuck (and, with qa_task_id a plain column, nothing in the database objects). Deleting a QA task
        // therefore sends its parent back to Active with the dead link cleared, ready to be sent to QA again.
        if (task.ParentTaskId is Guid parentId)
        {
            var parent = await _tasks.GetByIdAsync(parentId, ct);
            if (parent is not null && parent.QaTaskId == task.Id)
            {
                if (parent.Status == Domain.Tasks.TaskStatus.InQa)
                {
                    parent.ReturnFromQaWithoutQaTask(cmd.ActorId, "Its QA task was deleted");
                    notes.Add($"parent task {parent.Id} returned from QA to Active");
                }
                else
                {
                    parent.SetQaTaskId(null);
                    notes.Add($"parent task {parent.Id} unlinked");
                }
            }
        }

        // And a task with a QA task takes it with it, rather than leaving a review for work that no longer exists.
        if (task.QaTaskId is Guid qaTaskId)
        {
            var qaTask = await _tasks.GetByIdAsync(qaTaskId, ct);
            if (qaTask is not null)
            {
                await _tasks.DeleteAsync(qaTask, ct);
                notes.Add($"its QA task {qaTask.Id} deleted too");
            }
        }

        await _tasks.DeleteAsync(task, ct);
        await _tasks.SaveChangesAsync(ct);

        await _audit.LogAsync("TASK_DELETED", cmd.ActorId, cmd.IpAddress,
            $"Deleted task {cmd.TaskId} '{task.Title}'" + (notes.Count > 0 ? $" ({string.Join("; ", notes)})" : ""), ct);

        return ServiceResult<bool>.Ok(true);
    }
}
