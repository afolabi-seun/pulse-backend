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

        await _tasks.DeleteAsync(task, ct);
        await _tasks.SaveChangesAsync(ct);

        await _audit.LogAsync("TASK_DELETED", cmd.ActorId, cmd.IpAddress,
            $"Deleted task {cmd.TaskId} '{task.Title}'", ct);

        return ServiceResult<bool>.Ok(true);
    }
}
