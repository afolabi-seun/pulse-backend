using System.Text.Json;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Notifications;
using Pulse.Domain.Common;
using Pulse.Domain.Notifications;
using MediatR;

namespace Pulse.Application.Tasks.Commands;

/// <summary>The assignee's reply to a pending QA rejection — called on the original task itself
/// (not the QA sub-task), matching where the assignee actually works. Same access model as
/// FlagBlockerCommand: the assignee themselves, or anyone with general access to the project.</summary>
public record RespondToQaRejectionCommand(
    Guid TaskId,
    string Response,
    Guid ActorId,
    string? IpAddress,
    string ActorRole = "") : IRequest<ServiceResult<TaskDto>>;

public class RespondToQaRejectionHandler : IRequestHandler<RespondToQaRejectionCommand, ServiceResult<TaskDto>>
{
    private readonly ITaskRepository _tasks;
    private readonly IAuditLogRepository _audit;
    private readonly IProjectAccessPolicy _access;
    private readonly IEngineerRepository _engineers;
    private readonly INotificationDispatcher _notify;

    public RespondToQaRejectionHandler(
        ITaskRepository tasks,
        IAuditLogRepository audit,
        IProjectAccessPolicy access,
        IEngineerRepository engineers,
        INotificationDispatcher notify)
    {
        _notify = notify;
        _tasks = tasks;
        _audit = audit;
        _access = access;
        _engineers = engineers;
    }

    public async Task<ServiceResult<TaskDto>> Handle(RespondToQaRejectionCommand cmd, CancellationToken ct)
    {
        var task = await _tasks.GetByIdAsync(cmd.TaskId, ct);
        if (task is null)
            return ServiceResult<TaskDto>.Fail("NOT_FOUND", $"Task '{cmd.TaskId}' not found.");

        if (task.AssigneeId != cmd.ActorId
            && !await _access.CanAccessProjectAsync(task.ProjectId, cmd.ActorId, cmd.ActorRole, ct))
            return ServiceResult<TaskDto>.Fail("FORBIDDEN", "You do not have access to this task.");

        try
        {
            task.RespondToQaRejection(cmd.Response, cmd.ActorId);
        }
        catch (DomainException ex)
        {
            return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION", ex.Message);
        }

        await _tasks.SaveChangesAsync(ct);

        var responder = await _engineers.GetByIdAsync(cmd.ActorId, ct);

        await _audit.LogAsync("QA_REJECTION_RESPONSE_SUBMITTED", cmd.ActorId, cmd.IpAddress,
            $"Response submitted for pending QA rejection on task '{task.Id}'.", ct);

        if (task.PendingRejectionActorId is Guid reviewerId)
        {
            var payload = JsonSerializer.Serialize(new
            {
                taskId       = task.Id,
                taskTitle    = task.Title,
                response     = cmd.Response,
                respondedBy  = responder?.Name,
            });

            await _notify.NotifyAsync(reviewerId, NotificationKind.QaRejectionResponded, payload, ct: ct);
        }

        return ServiceResult<TaskDto>.Ok(TaskDto.From(task, pendingRejectionRespondedByName: responder?.Name));
    }
}
