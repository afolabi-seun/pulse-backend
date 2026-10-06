using System.Text.Json;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Notifications;
using Pulse.Domain.Common;
using Pulse.Domain.Notifications;
using MediatR;

namespace Pulse.Application.Tasks.Commands;

public record RejectPrApprovalCommand(Guid TaskId, string? Reason, Guid ActorId, string? IpAddress, string ActorRole = "")
    : IRequest<ServiceResult<TaskDto>>;

public class RejectPrApprovalHandler : IRequestHandler<RejectPrApprovalCommand, ServiceResult<TaskDto>>
{
    private readonly ITaskRepository _tasks;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;
    private readonly IAuditLogRepository _audit;
    private readonly INotificationDispatcher _notify;
    private readonly IAppSettings _settings;

    public RejectPrApprovalHandler(
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

    public async Task<ServiceResult<TaskDto>> Handle(RejectPrApprovalCommand cmd, CancellationToken ct)
    {
        var task = await _tasks.GetByIdAsync(cmd.TaskId, ct);
        if (task is null)
            return ServiceResult<TaskDto>.Fail("NOT_FOUND", $"Task '{cmd.TaskId}' not found.");

        if (!task.PendingPrApprovalRequestedAt.HasValue)
            return ServiceResult<TaskDto>.Fail("NOT_PENDING", "No PR approval request is currently pending for this task.");

        if (!await PrApprovalPolicy.IsAuthorizedAsync(task, cmd.ActorId, cmd.ActorRole, _engineers, _teams, ct))
            return ServiceResult<TaskDto>.Fail("FORBIDDEN",
                "Only the assignee's department head (or a Team Lead, if none is set up), or whoever this request was reassigned to, can reject it.");

        var requestedBy = task.PendingPrApprovalRequestedByEngineerId;

        try
        {
            task.RejectPrApproval(cmd.ActorId);
        }
        catch (DomainException ex)
        {
            return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION", ex.Message);
        }

        await _tasks.SaveChangesAsync(ct);

        await _audit.LogAsync("PR_APPROVAL_REJECTED", cmd.ActorId, cmd.IpAddress,
            $"PR approval rejected for task '{task.Id}'.{(cmd.Reason is not null ? $" Reason: {cmd.Reason}" : "")}", ct);

        if (requestedBy is Guid submitterId)
        {
            var rejector = await _engineers.GetByIdAsync(cmd.ActorId, ct);
            var payload = JsonSerializer.Serialize(new
            {
                taskId = task.Id, taskTitle = task.Title,
                rejectedByName = rejector?.Name, reason = cmd.Reason,
            });
            await _notify.NotifyAsync(submitterId, NotificationKind.PrApprovalRejected, payload, ct: ct);

            var submitter = await _engineers.GetByIdAsync(submitterId, ct);
            if (submitter is not null)
            {
                var taskLink = $"{_settings.AppBaseUrl}/tasks/{task.Id}";
                var reasonHtml = string.IsNullOrWhiteSpace(cmd.Reason) ? string.Empty : $"""
                    <p style="background:#fef2f2;border-left:4px solid #ef4444;padding:12px 16px;border-radius:4px;color:#991b1b;">
                      <strong>Reason:</strong> {System.Net.WebUtility.HtmlEncode(cmd.Reason)}
                    </p>
                    """;
                var body = $"""
                    <p>Hi {submitter.Name},</p>
                    <p><strong>{rejector?.Name ?? "The department head"}</strong> rejected the PR for
                    <strong>{task.Title}</strong>. Submit a fresh PR link to request approval again.</p>
                    {reasonHtml}
                    {EmailTemplate.Button(taskLink, "View task")}
                    """;
                await _notify.EmailAsync(submitter.Id, NotificationKind.PrApprovalRejected, new NotificationEmail(submitter.Email, $"PR rejected: {task.Title}", EmailTemplate.Layout(body)), ct);
            }
        }

        return ServiceResult<TaskDto>.Ok(TaskDto.From(task));
    }
}
