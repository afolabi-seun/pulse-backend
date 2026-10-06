using System.Text.Json;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Notifications;
using Pulse.Domain.Engineers;
using Pulse.Domain.Notifications;
using MediatR;

namespace Pulse.Application.Tasks.Commands;

public record LoanSubtaskCommand(
    Guid TaskId,
    Guid SubtaskId,
    Guid TargetEngineerId,
    string? Reason,
    Guid ActorId,
    string? IpAddress,
    string ActorRole = "") : IRequest<ServiceResult<SubtaskDto>>;

public class LoanSubtaskHandler : IRequestHandler<LoanSubtaskCommand, ServiceResult<SubtaskDto>>
{
    private readonly ITaskRepository _tasks;
    private readonly ISubtaskRepository _subtasks;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;
    private readonly IAuditLogRepository _audit;
    private readonly IProjectRepository _projects;
    private readonly INotificationDispatcher _notify;
    private readonly IAppSettings _settings;

    public LoanSubtaskHandler(
        ITaskRepository tasks,
        ISubtaskRepository subtasks,
        IEngineerRepository engineers,
        ITeamRepository teams,
        IAuditLogRepository audit,
        IProjectRepository projects,
        INotificationDispatcher notify,
        IAppSettings settings)
    {
        _tasks = tasks;
        _subtasks = subtasks;
        _engineers = engineers;
        _teams = teams;
        _audit = audit;
        _projects = projects;
        _notify = notify;
        _settings = settings;
    }

    public async Task<ServiceResult<SubtaskDto>> Handle(LoanSubtaskCommand cmd, CancellationToken ct)
    {
        var subtask = await _subtasks.GetByIdAsync(cmd.SubtaskId, ct);
        if (subtask is null || subtask.TaskId != cmd.TaskId)
            return ServiceResult<SubtaskDto>.Fail("NOT_FOUND", $"Subtask '{cmd.SubtaskId}' not found.");

        var task = await _tasks.GetByIdAsync(cmd.TaskId, ct);
        if (task is null)
            return ServiceResult<SubtaskDto>.Fail("NOT_FOUND", $"Task '{cmd.TaskId}' not found.");

        if (task.AssigneeId is null)
            return ServiceResult<SubtaskDto>.Fail("BUSINESS_RULE_VIOLATION",
                "Cannot loan a subtask on an unassigned task. Assign the task to one of your engineers first.");

        var allTeams = await _teams.ListAllAsync(ct);
        // The Team Lead eligibility check below is deliberately anchored to the TASK's real
        // assignee (where the work actually lives), not the subtask's current holder — same as
        // RecallSubtaskCommand's identical check.
        var taskAssignee = await _engineers.GetByIdAsync(task.AssigneeId.Value, ct);

        // Mirrors LoanTaskCommand's actor check exactly: a plain Team Lead may only loan work
        // currently sitting on their own team; Head/PMO roles are unrestricted.
        if (!Roles.HeadRoles.Contains(cmd.ActorRole) && cmd.ActorRole != Roles.ProjectManager)
        {
            var actorTeam = allTeams.FirstOrDefault(t => t.TeamLeadId == cmd.ActorId);
            if (actorTeam is null)
                return ServiceResult<SubtaskDto>.Fail("BUSINESS_RULE_VIOLATION",
                    "You do not lead any team. Only Team Leads who are the designated lead of a team may loan subtasks.");

            if (taskAssignee?.TeamId != actorTeam.Id)
                return ServiceResult<SubtaskDto>.Fail("BUSINESS_RULE_VIOLATION",
                    "You can only loan subtasks on tasks currently assigned to engineers on your team.");
        }

        var target = await _engineers.GetByIdAsync(cmd.TargetEngineerId, ct);
        if (target is null || !target.IsActive)
            return ServiceResult<SubtaskDto>.Fail("BUSINESS_RULE_VIOLATION", "Target engineer not found or inactive.");

        var targetTeam = target.TeamId.HasValue
            ? allTeams.FirstOrDefault(t => t.Id == target.TeamId.Value)
            : null;

        // The department-conflict check compares against whoever CURRENTLY holds the subtask
        // (falling back to the task's assignee if it's never been loaned) — not always the task's
        // original assignee, so a re-loan is judged against where the work sits right now, the
        // same way LoanTaskCommand's check naturally tracks a task's current holder.
        var currentHolderId = subtask.AssigneeId ?? task.AssigneeId.Value;
        var currentHolder = currentHolderId == taskAssignee?.Id ? taskAssignee : await _engineers.GetByIdAsync(currentHolderId, ct);
        var currentHolderTeam = currentHolder?.TeamId.HasValue == true
            ? allTeams.FirstOrDefault(t => t.Id == currentHolder.TeamId.Value)
            : null;

        // "Loan" means moving work across a department boundary — compare the target against the
        // current holder's department (a subtask has no department of its own before its first
        // loan, hence the task-assignee fallback above), same idea as LoanTaskCommand.
        var sameDepartment = currentHolderTeam?.Department is not null
            ? targetTeam?.Department == currentHolderTeam.Department
            : targetTeam is null || targetTeam.Id == currentHolderTeam?.Id;

        if (sameDepartment)
            return ServiceResult<SubtaskDto>.Fail("BUSINESS_RULE_VIOLATION",
                "Target engineer is already in the same department as the subtask's current holder. Use a regular reassignment instead.");

        subtask.Loan(cmd.TargetEngineerId);
        await _subtasks.SaveChangesAsync(ct);

        // A loan crosses team boundaries — grant the borrower access to the project so they can
        // reach the task the subtask lives on (idempotent, additive).
        await _projects.AddMemberAsync(task.ProjectId, cmd.TargetEngineerId, ct);

        var fromDescription = currentHolderTeam is not null ? $"team '{currentHolderTeam.Id}'" : "an unscoped assignee";
        var detail = cmd.Reason is not null ? $" — {cmd.Reason}" : string.Empty;
        await _audit.LogAsync("SUBTASK_LOANED", cmd.ActorId, cmd.IpAddress,
            $"Subtask '{subtask.Id}' on task '{task.Id}' loaned from {fromDescription} to engineer '{cmd.TargetEngineerId}'{detail}", ct);

        await PushAsync(cmd.TargetEngineerId, NotificationKind.SubtaskLoaned,
            new { taskId = task.Id, subtaskId = subtask.Id, taskTitle = task.Title, subtaskTitle = subtask.Title, reason = cmd.Reason }, ct);

        var taskLink = $"{_settings.AppBaseUrl}/tasks/{task.Id}";
        var body = $"""
            <p>Hi {target.Name},</p>
            <p>A checklist item has been loaned to you from another department: <strong>{subtask.Title}</strong> (on task <strong>{task.Title}</strong>).</p>
            {(cmd.Reason is not null ? $"<p>Reason: <strong>{cmd.Reason}</strong></p>" : "")}
            {EmailTemplate.Button(taskLink, "View task")}
            {EmailTemplate.Muted("This notification was sent because a subtask was loaned to you.")}
            """;
        await _notify.EmailAsync(target.Id, NotificationKind.SubtaskLoaned, new NotificationEmail(target.Email, $"Subtask loaned to you: {subtask.Title}", EmailTemplate.Layout(body)), ct);

        return ServiceResult<SubtaskDto>.Ok(SubtaskDto.From(subtask, target.Name));
    }

    private Task PushAsync(Guid userId, string kind, object payload, CancellationToken ct) =>
        _notify.NotifyAsync(userId, kind, payload, ct: ct);
}
