using System.Text.Json;
using Pulse.Application.Auth;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Notifications;
using Pulse.Domain.Notifications;
using MediatR;

namespace Pulse.Application.Estimation.Commands;

public record ApproveEstimateCommand(Guid TaskId, Guid ActorId, string ActorRole) : IRequest<ServiceResult<Unit>>;

public class ApproveEstimateHandler : IRequestHandler<ApproveEstimateCommand, ServiceResult<Unit>>
{
    private readonly IEstimationRepository _estimation;
    private readonly ITaskRepository _tasks;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;
    private readonly INotificationRepository _notifications;
    private readonly IRealtimeNotifier _realtime;
    private readonly IEmailQueue _emailQueue;
    private readonly IAppSettings _settings;

    public ApproveEstimateHandler(
        IEstimationRepository estimation, ITaskRepository tasks, IEngineerRepository engineers, ITeamRepository teams,
        INotificationRepository notifications, IRealtimeNotifier realtime, IEmailQueue emailQueue, IAppSettings settings)
    {
        _estimation = estimation;
        _tasks = tasks;
        _engineers = engineers;
        _teams = teams;
        _notifications = notifications;
        _realtime = realtime;
        _emailQueue = emailQueue;
        _settings = settings;
    }

    public async Task<ServiceResult<Unit>> Handle(ApproveEstimateCommand request, CancellationToken ct)
    {
        var session = await _estimation.GetSessionAsync(request.TaskId, ct);
        if (session?.PendingApprovalPoints is not int points)
            return ServiceResult<Unit>.Fail("NOT_PENDING", "No estimate is currently pending approval for this task.");

        var task = await _tasks.GetByIdAsync(request.TaskId, ct);
        if (task is null)
            return ServiceResult<Unit>.Fail("NOT_FOUND", "Task not found.");

        if (!await EstimationApproval.IsAuthorizedAsync(task.AssigneeId, request.ActorId, request.ActorRole, session.EscalatedToHead, _engineers, _teams, ct))
            return ServiceResult<Unit>.Fail("FORBIDDEN", "Only the assignee's department head (or a Team Lead, if none is set up) can approve this estimate.");

        task.SetPoints(points, request.ActorId, reason: "Re-estimated with Planning Poker and approved");
        await _tasks.SaveChangesAsync(ct);
        await _estimation.DeleteSessionAndVotesAsync(request.TaskId, ct);

        if (session.SubmittedBy is Guid submittedBy)
        {
            var approver = await _engineers.GetByIdAsync(request.ActorId, ct);
            var payload = JsonSerializer.Serialize(new { taskId = task.Id, taskTitle = task.Title, points, approvedByName = approver?.Name });
            var n = Notification.Create(submittedBy, NotificationKind.EstimateApproved, payload, NotificationChannel.InApp);
            await _notifications.AddAsync(n, ct);
            await _notifications.SaveChangesAsync(ct);
            await _realtime.SendNotificationAsync(submittedBy, NotificationDto.From(n), ct);

            var submitter = await _engineers.GetByIdAsync(submittedBy, ct);
            if (submitter is not null)
            {
                var taskLink = $"{_settings.AppBaseUrl}/tasks/{task.Id}";
                var body = $"""
                    <p>Hi {submitter.Name},</p>
                    <p><strong>{approver?.Name ?? "The department head"}</strong> approved your estimate of
                    <strong>{points} pts</strong> for <strong>{task.Title}</strong> — it's now set on the task.</p>
                    {EmailTemplate.Button(taskLink, "View task")}
                    """;
                _emailQueue.Enqueue(submitter.Email, $"Estimate approved: {task.Title}", EmailTemplate.Layout(body));
            }
        }

        return ServiceResult<Unit>.Ok(Unit.Value);
    }
}
