using System.Text.Json;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Notifications;
using Pulse.Application.Tasks;
using Pulse.Domain.Engineers;
using Pulse.Domain.Notifications;
using MediatR;

namespace Pulse.Application.Tasks.Commands;

public record LoanTaskCommand(
    Guid TaskId,
    Guid TargetEngineerId,
    string? Reason,
    Guid ActorId,
    string? IpAddress,
    string ActorRole = "") : IRequest<ServiceResult<TaskDto>>;

public class LoanTaskHandler : IRequestHandler<LoanTaskCommand, ServiceResult<TaskDto>>
{
    private readonly ITaskRepository _tasks;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;
    private readonly IAuditLogRepository _audit;
    private readonly IEscalationEventRepository _escalationEvents;
    private readonly IProjectRepository _projects;
    private readonly INotificationDispatcher _notify;
    private readonly IAppSettings _settings;

    public LoanTaskHandler(
        ITaskRepository tasks,
        IEngineerRepository engineers,
        ITeamRepository teams,
        IAuditLogRepository audit,
        IEscalationEventRepository escalationEvents,
        IProjectRepository projects,
        INotificationDispatcher notify,
        IAppSettings settings)
    {
        _tasks = tasks;
        _engineers = engineers;
        _teams = teams;
        _audit = audit;
        _escalationEvents = escalationEvents;
        _projects = projects;
        _notify = notify;
        _settings = settings;
    }

    public async Task<ServiceResult<TaskDto>> Handle(LoanTaskCommand cmd, CancellationToken ct)
    {
        var task = await _tasks.GetByIdAsync(cmd.TaskId, ct);
        if (task is null)
            return ServiceResult<TaskDto>.Fail("NOT_FOUND", $"Task '{cmd.TaskId}' not found.");

        if (task.AssigneeId is null)
            return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION",
                "Cannot loan an unassigned task. Assign it to one of your engineers first.");

        var allTeams = await _teams.ListAllAsync(ct);
        var currentAssignee = await _engineers.GetByIdAsync(task.AssigneeId.Value, ct);
        var currentAssigneeTeam = currentAssignee?.TeamId.HasValue == true
            ? allTeams.FirstOrDefault(t => t.Id == currentAssignee.TeamId.Value)
            : null;

        // A department head or PMO (Head of PMO / Project Manager) isn't the designated lead of
        // any one team — they already have company-wide reach everywhere else in the app, so
        // "must be your own team's task" doesn't apply to them the way it does to a plain Team
        // Lead. Only a plain Team Lead is restricted to loaning tasks currently on the team they
        // actually lead.
        if (!Roles.HeadRoles.Contains(cmd.ActorRole) && cmd.ActorRole != Roles.ProjectManager)
        {
            var actorTeam = allTeams.FirstOrDefault(t => t.TeamLeadId == cmd.ActorId);
            if (actorTeam is null)
                return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION",
                    "You do not lead any team. Only Team Leads who are the designated lead of a team may loan tasks.");

            if (currentAssignee?.TeamId != actorTeam.Id)
                return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION",
                    "You can only loan tasks currently assigned to engineers on your team.");
        }

        // Verify the target engineer exists, is active, and passes QA-assignment rules.
        var target = await _engineers.GetByIdAsync(cmd.TargetEngineerId, ct);
        if (target is null || !target.IsActive)
            return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION",
                "Target engineer not found or inactive.");
        var qaCheck = await QaAssignmentPolicy.ValidateAsync(task, target, cmd.ActorRole, _tasks, _teams, ct);
        if (!qaCheck.IsAllowed)
            return ServiceResult<TaskDto>.Fail(qaCheck.ErrorCode!, qaCheck.ErrorMessage!);

        var targetTeam = target.TeamId.HasValue
            ? allTeams.FirstOrDefault(t => t.Id == target.TeamId.Value)
            : null;

        // "Loan" specifically means moving work across a department boundary — compare the
        // target against the task's current assignee's department, not the actor's. A global
        // actor (department head, Head of PMO) has no department of their own to compare
        // against, so the comparison always has to be relative to where the work already sits.
        var sameDepartment = currentAssigneeTeam?.Department is not null
            ? targetTeam?.Department == currentAssigneeTeam.Department
            : targetTeam is null || targetTeam.Id == currentAssigneeTeam?.Id;

        if (sameDepartment)
            return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION",
                "Target engineer is already in the same department as the current assignee. Use a regular reassignment instead.");

        task.Loan(cmd.TargetEngineerId, cmd.ActorId);
        await _tasks.SaveChangesAsync(ct);

        // A loan crosses team boundaries — grant the borrower access to the project so
        // they can see the task's context (idempotent, additive).
        await _projects.AddMemberAsync(task.ProjectId, cmd.TargetEngineerId, ct);

        // Re-arm escalation events so the scanner uses the new engineer's fresh clock.
        await _escalationEvents.ClearForTaskAsync(task.Id, ct);

        var fromDescription = currentAssigneeTeam is not null ? $"team '{currentAssigneeTeam.Id}'" : "an unscoped assignee";
        var detail = cmd.Reason is not null ? $" — {cmd.Reason}" : string.Empty;
        await _audit.LogAsync("TASK_LOANED", cmd.ActorId, cmd.IpAddress,
            $"Task '{task.Id}' loaned from {fromDescription} to engineer '{cmd.TargetEngineerId}'{detail}", ct);

        await PushAsync(cmd.TargetEngineerId, NotificationKind.TaskLoaned,
            new { taskId = task.Id, taskTitle = task.Title, reason = cmd.Reason }, ct);

        var taskLink = $"{_settings.AppBaseUrl}/tasks/{task.Id}";
        var body = $"""
            <p>Hi {target.Name},</p>
            <p>A task has been loaned to you from another department: <strong>{task.Title}</strong>.</p>
            {(cmd.Reason is not null ? $"<p>Reason: <strong>{cmd.Reason}</strong></p>" : "")}
            {EmailTemplate.Button(taskLink, "View task")}
            {EmailTemplate.Muted("This notification was sent because a task was loaned to you.")}
            """;
        await _notify.EmailAsync(target.Id, NotificationKind.TaskLoaned, new NotificationEmail(target.Email, $"Task loaned to you: {task.Title}", EmailTemplate.Layout(body)), ct);

        return ServiceResult<TaskDto>.Ok(TaskDto.From(task));
    }

    private Task PushAsync(Guid userId, string kind, object payload, CancellationToken ct) =>
        _notify.NotifyAsync(userId, kind, payload, ct: ct);
}
