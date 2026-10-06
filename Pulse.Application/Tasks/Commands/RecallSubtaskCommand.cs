using System.Text.Json;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Notifications;
using Pulse.Domain.Engineers;
using Pulse.Domain.Notifications;
using MediatR;

namespace Pulse.Application.Tasks.Commands;

public record RecallSubtaskCommand(
    Guid TaskId,
    Guid SubtaskId,
    Guid ActorId,
    string? IpAddress,
    string ActorRole = "") : IRequest<ServiceResult<SubtaskDto>>;

public class RecallSubtaskHandler : IRequestHandler<RecallSubtaskCommand, ServiceResult<SubtaskDto>>
{
    private readonly ITaskRepository _tasks;
    private readonly ISubtaskRepository _subtasks;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;
    private readonly IAuditLogRepository _audit;
    private readonly INotificationDispatcher _notify;
    private readonly IAppSettings _settings;

    public RecallSubtaskHandler(
        ITaskRepository tasks,
        ISubtaskRepository subtasks,
        IEngineerRepository engineers,
        ITeamRepository teams,
        IAuditLogRepository audit,
        INotificationDispatcher notify,
        IAppSettings settings)
    {
        _tasks = tasks;
        _subtasks = subtasks;
        _engineers = engineers;
        _teams = teams;
        _audit = audit;
        _notify = notify;
        _settings = settings;
    }

    public async Task<ServiceResult<SubtaskDto>> Handle(RecallSubtaskCommand cmd, CancellationToken ct)
    {
        var subtask = await _subtasks.GetByIdAsync(cmd.SubtaskId, ct);
        if (subtask is null || subtask.TaskId != cmd.TaskId)
            return ServiceResult<SubtaskDto>.Fail("NOT_FOUND", $"Subtask '{cmd.SubtaskId}' not found.");

        if (subtask.AssigneeId is null)
            return ServiceResult<SubtaskDto>.Fail("BUSINESS_RULE_VIOLATION",
                "This subtask is not currently loaned to anyone.");

        var task = await _tasks.GetByIdAsync(cmd.TaskId, ct);
        if (task is null)
            return ServiceResult<SubtaskDto>.Fail("NOT_FOUND", $"Task '{cmd.TaskId}' not found.");

        var borrowerId = subtask.AssigneeId.Value;

        // Same eligibility basis as LoanSubtaskCommand: a plain Team Lead may only recall
        // subtasks on tasks currently owned by their own team; Head/PMO roles are unrestricted.
        // The subtask's current holder (whoever it's loaned to) can also always recall it
        // themselves, same as the task-level self-recall.
        if (cmd.ActorId != borrowerId
            && !Roles.HeadRoles.Contains(cmd.ActorRole) && cmd.ActorRole != Roles.ProjectManager)
        {
            var allTeams = await _teams.ListAllAsync(ct);
            var currentAssignee = task.AssigneeId.HasValue ? await _engineers.GetByIdAsync(task.AssigneeId.Value, ct) : null;
            var actorTeam = allTeams.FirstOrDefault(t => t.TeamLeadId == cmd.ActorId);
            if (actorTeam is null || currentAssignee?.TeamId != actorTeam.Id)
                return ServiceResult<SubtaskDto>.Fail("BUSINESS_RULE_VIOLATION",
                    "You can only recall subtasks on tasks currently assigned to engineers on your team.");
        }

        subtask.Recall();
        await _subtasks.SaveChangesAsync(ct);

        await _audit.LogAsync("SUBTASK_RECALLED", cmd.ActorId, cmd.IpAddress,
            $"Subtask '{subtask.Id}' on task '{task.Id}' recalled from engineer '{borrowerId}'", ct);

        await PushAsync(borrowerId, NotificationKind.SubtaskRecalled,
            new { taskId = task.Id, subtaskId = subtask.Id, taskTitle = task.Title, subtaskTitle = subtask.Title }, ct);

        var borrower = await _engineers.GetByIdAsync(borrowerId, ct);
        if (borrower is not null)
        {
            var taskLink = $"{_settings.AppBaseUrl}/tasks/{task.Id}";
            var body = $"""
                <p>Hi {borrower.Name},</p>
                <p>A checklist item that was loaned to you has been recalled: <strong>{subtask.Title}</strong> (on task <strong>{task.Title}</strong>).</p>
                {EmailTemplate.Button(taskLink, "View task")}
                {EmailTemplate.Muted("This notification was sent because a subtask loaned to you was recalled.")}
                """;
            await _notify.EmailAsync(borrower.Id, NotificationKind.SubtaskRecalled, new NotificationEmail(borrower.Email, $"Subtask recalled: {subtask.Title}", EmailTemplate.Layout(body)), ct);
        }

        return ServiceResult<SubtaskDto>.Ok(SubtaskDto.From(subtask));
    }

    private Task PushAsync(Guid userId, string kind, object payload, CancellationToken ct) =>
        _notify.NotifyAsync(userId, kind, payload, ct: ct);
}
