using System.Text.Json;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Notifications;
using Pulse.Domain.Common;
using Pulse.Domain.Notifications;
using MediatR;

namespace Pulse.Application.Tasks.Commands;

public record RequestPrApprovalCommand(Guid TaskId, string PrLink, Guid ActorId, string? IpAddress, string ActorRole = "")
    : IRequest<ServiceResult<TaskDto>>;

public class RequestPrApprovalHandler : IRequestHandler<RequestPrApprovalCommand, ServiceResult<TaskDto>>
{
    private readonly ITaskRepository _tasks;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;
    private readonly IProjectAccessPolicy _access;
    private readonly IAuditLogRepository _audit;
    private readonly INotificationRepository _notifications;
    private readonly IRealtimeNotifier _realtime;
    private readonly IEmailQueue _emailQueue;
    private readonly IAppSettings _settings;

    public RequestPrApprovalHandler(
        ITaskRepository tasks, IEngineerRepository engineers, ITeamRepository teams, IProjectAccessPolicy access,
        IAuditLogRepository audit, INotificationRepository notifications, IRealtimeNotifier realtime,
        IEmailQueue emailQueue, IAppSettings settings)
    {
        _tasks = tasks;
        _engineers = engineers;
        _teams = teams;
        _access = access;
        _audit = audit;
        _notifications = notifications;
        _realtime = realtime;
        _emailQueue = emailQueue;
        _settings = settings;
    }

    public async Task<ServiceResult<TaskDto>> Handle(RequestPrApprovalCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.PrLink))
            return ServiceResult<TaskDto>.Fail("INVALID_PR_LINK", "A PR link is required.");

        var task = await _tasks.GetByIdAsync(cmd.TaskId, ct);
        if (task is null)
            return ServiceResult<TaskDto>.Fail("NOT_FOUND", $"Task '{cmd.TaskId}' not found.");

        if (task.AssigneeId != cmd.ActorId
            && !await _access.CanAccessProjectAsync(task.ProjectId, cmd.ActorId, cmd.ActorRole, ct))
            return ServiceResult<TaskDto>.Fail("FORBIDDEN", "You do not have access to this task.");

        try
        {
            task.RequestPrApproval(cmd.PrLink, cmd.ActorId);
        }
        catch (DomainException ex)
        {
            return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION", ex.Message);
        }

        await _tasks.SaveChangesAsync(ct);

        await _audit.LogAsync("PR_APPROVAL_REQUESTED", cmd.ActorId, cmd.IpAddress,
            $"PR approval requested for task '{task.Id}' ({cmd.PrLink}).", ct);

        await NotifyDepartmentHeadsAsync(task, cmd, ct);

        return ServiceResult<TaskDto>.Ok(TaskDto.From(task));
    }

    private async Task NotifyDepartmentHeadsAsync(Domain.Tasks.PulseTask task, RequestPrApprovalCommand cmd, CancellationToken ct)
    {
        if (task.AssigneeId is not Guid assigneeId) return;

        var heads = await DepartmentScope.GetDepartmentHeadsAsync(assigneeId, _engineers, _teams, ct);
        if (heads.Count == 0) return;

        var requester = await _engineers.GetByIdAsync(cmd.ActorId, ct);
        var taskLink = $"{_settings.AppBaseUrl}/tasks/{task.Id}";

        foreach (var head in heads)
        {
            var payload = JsonSerializer.Serialize(new
            {
                taskId = task.Id,
                taskTitle = task.Title,
                prLink = cmd.PrLink,
                requestedByName = requester?.Name,
            });

            var n = Notification.Create(head.Id, NotificationKind.PrApprovalRequested, payload, NotificationChannel.InApp);
            await _notifications.AddAsync(n, ct);
            await _notifications.SaveChangesAsync(ct);
            await _realtime.SendNotificationAsync(head.Id, NotificationDto.From(n), ct);

            var body = $"""
                <p>Hi {head.Name},</p>
                <p><strong>{requester?.Name ?? "An engineer"}</strong> submitted a pull request for
                <strong>{task.Title}</strong> and it needs your approval.</p>
                <p>PR: <strong>{cmd.PrLink}</strong></p>
                {EmailTemplate.Button(taskLink, "Review task")}
                {EmailTemplate.Muted("This notification was sent because you are the head of this engineer's department.")}
                """;
            _emailQueue.Enqueue(head.Email, $"PR approval needed: {task.Title}", EmailTemplate.Layout(body));
        }
    }
}
