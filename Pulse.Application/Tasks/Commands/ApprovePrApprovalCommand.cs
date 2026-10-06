using System.Text.Json;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Notifications;
using Pulse.Domain.Common;
using Pulse.Domain.Notifications;
using MediatR;

namespace Pulse.Application.Tasks.Commands;

public record ApprovePrApprovalCommand(Guid TaskId, Guid ActorId, string? IpAddress, string ActorRole = "")
    : IRequest<ServiceResult<TaskDto>>;

public class ApprovePrApprovalHandler : IRequestHandler<ApprovePrApprovalCommand, ServiceResult<TaskDto>>
{
    private readonly ITaskRepository _tasks;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;
    private readonly IAuditLogRepository _audit;
    private readonly INotificationDispatcher _notify;
    private readonly IAppSettings _settings;

    public ApprovePrApprovalHandler(
        ITaskRepository tasks,
        IEngineerRepository engineers,
        ITeamRepository teams,
        IAuditLogRepository audit,
        IAppSettings settings,
        INotificationDispatcher notify)
    {
        _tasks = tasks;
        _engineers = engineers;
        _teams = teams;
        _audit = audit;
        _notify = notify;
        _settings = settings;
    }

    public async Task<ServiceResult<TaskDto>> Handle(ApprovePrApprovalCommand cmd, CancellationToken ct)
    {
        var task = await _tasks.GetByIdAsync(cmd.TaskId, ct);
        if (task is null)
            return ServiceResult<TaskDto>.Fail("NOT_FOUND", $"Task '{cmd.TaskId}' not found.");

        if (!task.PendingPrApprovalRequestedAt.HasValue)
            return ServiceResult<TaskDto>.Fail("NOT_PENDING", "No PR approval request is currently pending for this task.");

        if (!await PrApprovalPolicy.IsAuthorizedAsync(task, cmd.ActorId, cmd.ActorRole, _engineers, _teams, ct))
            return ServiceResult<TaskDto>.Fail("FORBIDDEN",
                "Only the assignee's department head (or a Team Lead, if none is set up), or whoever this request was reassigned to, can approve it.");

        var requestedBy = task.PendingPrApprovalRequestedByEngineerId;

        try
        {
            task.ApprovePrApproval(cmd.ActorId);
        }
        catch (DomainException ex)
        {
            return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION", ex.Message);
        }

        await _tasks.SaveChangesAsync(ct);

        await _audit.LogAsync("PR_APPROVAL_APPROVED", cmd.ActorId, cmd.IpAddress,
            $"PR approval granted for task '{task.Id}'.", ct);

        if (requestedBy is Guid submitterId)
        {
            var approver = await _engineers.GetByIdAsync(cmd.ActorId, ct);
            var payload = JsonSerializer.Serialize(new { taskId = task.Id, taskTitle = task.Title, approvedByName = approver?.Name });
            await _notify.NotifyAsync(submitterId, NotificationKind.PrApprovalApproved, payload, ct: ct);

            var submitter = await _engineers.GetByIdAsync(submitterId, ct);
            if (submitter is not null)
            {
                var taskLink = $"{_settings.AppBaseUrl}/tasks/{task.Id}";
                var body = $"""
                    <p>Hi {submitter.Name},</p>
                    <p><strong>{approver?.Name ?? "The department head"}</strong> approved the PR for
                    <strong>{task.Title}</strong> — it's ready to be marked done.</p>
                    {EmailTemplate.Button(taskLink, "View task")}
                    """;
                await _notify.EmailAsync(submitter.Id, NotificationKind.PrApprovalApproved, new NotificationEmail(submitter.Email, $"PR approved: {task.Title}", EmailTemplate.Layout(body)), ct);
            }
        }

        return ServiceResult<TaskDto>.Ok(TaskDto.From(task));
    }
}
