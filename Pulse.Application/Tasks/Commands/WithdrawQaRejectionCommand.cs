using System.Text.Json;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Notifications;
using Pulse.Domain.Common;
using Pulse.Domain.Notifications;
using MediatR;

namespace Pulse.Application.Tasks.Commands;

/// <summary>QA reconsiders (e.g. the assignee's response shows it was an environment issue) and
/// drops the proposed rejection — the task stays exactly where it was in QA, with no iteration
/// counted, and the linked QA sub-task stays open for review to continue.</summary>
public record WithdrawQaRejectionCommand(
    Guid QaTaskId,
    Guid ActorId,
    string? IpAddress,
    string ActorRole = "") : IRequest<ServiceResult<TaskDto>>;

public class WithdrawQaRejectionHandler : IRequestHandler<WithdrawQaRejectionCommand, ServiceResult<TaskDto>>
{
    private readonly ITaskRepository _tasks;
    private readonly IAuditLogRepository _audit;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;
    private readonly INotificationDispatcher _notify;

    public WithdrawQaRejectionHandler(
        ITaskRepository tasks,
        IAuditLogRepository audit,
        IEngineerRepository engineers,
        ITeamRepository teams,
        INotificationDispatcher notify)
    {
        _notify = notify;
        _tasks = tasks;
        _audit = audit;
        _engineers = engineers;
        _teams = teams;
    }

    public async Task<ServiceResult<TaskDto>> Handle(WithdrawQaRejectionCommand cmd, CancellationToken ct)
    {
        var qaTask = await _tasks.GetByIdAsync(cmd.QaTaskId, ct);
        if (qaTask is null)
            return ServiceResult<TaskDto>.Fail("NOT_FOUND", $"QA task '{cmd.QaTaskId}' not found.");

        if (!await QaRejectionPolicy.CanRejectAsync(qaTask, cmd.ActorId, cmd.ActorRole, _engineers, _teams, ct))
            return ServiceResult<TaskDto>.Fail("FORBIDDEN", "You do not have access to this task.");

        if (!qaTask.ParentTaskId.HasValue)
            return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION",
                "This task is not a QA task — it has no parent task.");

        var parentTask = await _tasks.GetByIdAsync(qaTask.ParentTaskId.Value, ct);
        if (parentTask is null)
            return ServiceResult<TaskDto>.Fail("NOT_FOUND", "Original task not found.");

        try
        {
            parentTask.WithdrawQaRejection(cmd.ActorId);
        }
        catch (DomainException ex)
        {
            return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION", ex.Message);
        }

        await _tasks.SaveChangesAsync(ct);

        await _audit.LogAsync("QA_REJECTION_WITHDRAWN", cmd.ActorId, cmd.IpAddress,
            $"QA rejection withdrawn for task '{parentTask.Id}'.", ct);

        if (parentTask.AssigneeId is Guid assigneeId)
        {
            var payload = JsonSerializer.Serialize(new { taskId = parentTask.Id, taskTitle = parentTask.Title });
            await _notify.NotifyAsync(assigneeId, NotificationKind.QaRejectionWithdrawn, payload, ct: ct);
        }

        return ServiceResult<TaskDto>.Ok(TaskDto.From(parentTask));
    }
}
