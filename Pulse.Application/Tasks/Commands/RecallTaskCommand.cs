using System.Text.Json;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Notifications;
using Pulse.Application.Tasks;
using Pulse.Domain.Engineers;
using Pulse.Domain.Notifications;
using MediatR;

namespace Pulse.Application.Tasks.Commands;

public record RecallTaskCommand(
    Guid TaskId,
    Guid ActorId,
    string? IpAddress,
    string ActorRole = "") : IRequest<ServiceResult<TaskDto>>;

public class RecallTaskHandler : IRequestHandler<RecallTaskCommand, ServiceResult<TaskDto>>
{
    private readonly ITaskRepository _tasks;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;
    private readonly IAuditLogRepository _audit;
    private readonly IEscalationEventRepository _escalationEvents;
    private readonly INotificationRepository _notifications;
    private readonly IRealtimeNotifier _realtime;
    private readonly IEmailQueue _emailQueue;
    private readonly IAppSettings _settings;

    public RecallTaskHandler(
        ITaskRepository tasks,
        IEngineerRepository engineers,
        ITeamRepository teams,
        IAuditLogRepository audit,
        IEscalationEventRepository escalationEvents,
        INotificationRepository notifications,
        IRealtimeNotifier realtime,
        IEmailQueue emailQueue,
        IAppSettings settings)
    {
        _tasks = tasks;
        _engineers = engineers;
        _teams = teams;
        _audit = audit;
        _escalationEvents = escalationEvents;
        _notifications = notifications;
        _realtime = realtime;
        _emailQueue = emailQueue;
        _settings = settings;
    }

    public async Task<ServiceResult<TaskDto>> Handle(RecallTaskCommand cmd, CancellationToken ct)
    {
        var task = await _tasks.GetByIdAsync(cmd.TaskId, ct);
        if (task is null)
            return ServiceResult<TaskDto>.Fail("NOT_FOUND", $"Task '{cmd.TaskId}' not found.");

        if (task.LoanedFromEngineerId is null)
            return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION",
                "This task is not currently on loan.");

        var originalAssignee = await _engineers.GetByIdAsync(task.LoanedFromEngineerId.Value, ct);
        if (originalAssignee is null || !originalAssignee.IsActive)
            return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION",
                "The original assignee is no longer available. Reassign this task manually instead.");

        // Mirrors LoanTaskHandler's own eligibility check, in reverse: a department head or PMO
        // (Head of PMO / Project Manager) can always recall, same as they can always loan; a
        // plain Team Lead can only recall a task back to their own team. The current holder
        // (whoever has the task out on loan) can also always recall it themselves — sending
        // borrowed work back doesn't need the lending team's sign-off.
        if (cmd.ActorId != task.AssigneeId
            && !Roles.HeadRoles.Contains(cmd.ActorRole) && cmd.ActorRole != Roles.ProjectManager)
        {
            var allTeams = await _teams.ListAllAsync(ct);
            var actorTeam = allTeams.FirstOrDefault(t => t.TeamLeadId == cmd.ActorId);
            if (actorTeam is null || originalAssignee.TeamId != actorTeam.Id)
                return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION",
                    "You can only recall tasks that were originally loaned from your own team.");
        }

        var borrowerId = task.AssigneeId;

        task.Recall(cmd.ActorId);
        await _tasks.SaveChangesAsync(ct);

        // Re-arm escalation events so the scanner uses the returning engineer's fresh clock.
        await _escalationEvents.ClearForTaskAsync(task.Id, ct);

        await _audit.LogAsync("TASK_RECALLED", cmd.ActorId, cmd.IpAddress,
            $"Task '{task.Id}' recalled back to engineer '{originalAssignee.Id}'", ct);

        if (borrowerId.HasValue)
        {
            await PushAsync(borrowerId.Value, NotificationKind.TaskRecalled,
                new { taskId = task.Id, taskTitle = task.Title }, ct);

            var borrower = await _engineers.GetByIdAsync(borrowerId.Value, ct);
            if (borrower is not null)
            {
                var taskLink = $"{_settings.AppBaseUrl}/tasks/{task.Id}";
                var body = $"""
                    <p>Hi {borrower.Name},</p>
                    <p>A task that was loaned to you has been recalled: <strong>{task.Title}</strong>.</p>
                    {EmailTemplate.Button(taskLink, "View task")}
                    {EmailTemplate.Muted("This notification was sent because a task loaned to you was recalled.")}
                    """;
                _emailQueue.Enqueue(borrower.Email, $"Task recalled: {task.Title}", EmailTemplate.Layout(body));
            }
        }

        return ServiceResult<TaskDto>.Ok(TaskDto.From(task));
    }

    private async Task PushAsync(Guid userId, string kind, object payload, CancellationToken ct)
    {
        var n = Notification.Create(userId, kind,
            JsonSerializer.Serialize(payload), NotificationChannel.InApp);
        await _notifications.AddAsync(n, ct);
        await _notifications.SaveChangesAsync(ct);
        await _realtime.SendNotificationAsync(userId, NotificationDto.From(n), ct);
    }
}
