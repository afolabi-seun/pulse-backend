using Pulse.Application.Auth;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Notifications;
using Pulse.Application.Tasks;
using Pulse.Domain.Engineers;
using Pulse.Domain.Notifications;
using MediatR;

namespace Pulse.Application.Tasks.Commands;

/// <summary>Assigns an engineer to a task that currently has none — the narrow "claim unassigned
/// work" action, deliberately separate from UpdateTaskCommand (which stays PmOrAbove-only) so a
/// Team Lead can be granted just this one capability without also unlocking title/points/due-date
/// edits on tasks outside their team. Reassigning an already-assigned task still goes through the
/// PM/head Edit form or, across departments, "Loan task". Claiming a task for yourself is open to
/// anyone with access to it — no controller-level capability gate — matching SendToQa/FlagBlocker;
/// assigning it to someone else remains Team-Lead-or-above, enforced inside the handler since that
/// distinction depends on the request body, not just the caller's role.</summary>
public record AssignTaskCommand(
    Guid TaskId,
    Guid AssigneeId,
    Guid ActorId,
    string? IpAddress,
    string ActorRole = "") : IRequest<ServiceResult<TaskDto>>;

public class AssignTaskHandler : IRequestHandler<AssignTaskCommand, ServiceResult<TaskDto>>
{
    private readonly ITaskRepository _tasks;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;
    private readonly IAuditLogRepository _audit;
    private readonly IProjectRepository _projects;
    private readonly IProjectAccessPolicy _access;
    private readonly INotificationDispatcher _notify;
    private readonly IAppSettings _settings;

    public AssignTaskHandler(
        ITaskRepository tasks,
        IEngineerRepository engineers,
        ITeamRepository teams,
        IAuditLogRepository audit,
        IProjectRepository projects,
        IProjectAccessPolicy access,
        INotificationDispatcher notify,
        IAppSettings settings)
    {
        _tasks = tasks;
        _engineers = engineers;
        _teams = teams;
        _audit = audit;
        _projects = projects;
        _access = access;
        _notify = notify;
        _settings = settings;
    }

    public async Task<ServiceResult<TaskDto>> Handle(AssignTaskCommand cmd, CancellationToken ct)
    {
        var task = await _tasks.GetByIdAsync(cmd.TaskId, ct);
        if (task is null)
            return ServiceResult<TaskDto>.Fail("NOT_FOUND", $"Task '{cmd.TaskId}' not found.");

        if (task.AssigneeId is not null)
            return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION",
                "This task already has an assignee — use reassignment or Loan task instead.");

        if (!await _access.CanAccessTaskAsync(cmd.TaskId, cmd.ActorId, cmd.ActorRole, ct))
            return ServiceResult<TaskDto>.Fail("FORBIDDEN", "You do not have access to this task.");

        // Claiming a sprint-committed task for yourself needs no extra capability beyond project
        // access; handing it to someone else is still Team-Lead-or-above.
        if (cmd.AssigneeId != cmd.ActorId
            && !CapabilityRegistry.ResolveFor(cmd.ActorRole).Contains(CapabilityRegistry.TeamLeadOrAbove))
            return ServiceResult<TaskDto>.Fail("FORBIDDEN", "You do not have access to this task.");

        if (task.DueDate is null)
            return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION",
                "A due date is required before assigning a task to an engineer.");

        var target = await _engineers.GetByIdAsync(cmd.AssigneeId, ct);
        if (target is null || !target.IsActive)
            return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION", "Assignee not found or inactive.");

        var qaCheck = await QaAssignmentPolicy.ValidateAsync(task, target, cmd.ActorRole, _tasks, _teams, ct);
        if (!qaCheck.IsAllowed)
            return ServiceResult<TaskDto>.Fail(qaCheck.ErrorCode!, qaCheck.ErrorMessage!);

        // A plain Team Lead may only claim work for their own team; every other role this
        // endpoint admits (PM-or-above) already has unrestricted assignment elsewhere.
        if (cmd.ActorRole == Roles.TeamLead)
        {
            var ledTeam = await DepartmentScope.GetLedTeamAsync(cmd.ActorId, _teams, ct);
            if (ledTeam is null)
                return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION",
                    "You do not lead any team.");
            if (target.TeamId != ledTeam.Id)
                return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION",
                    "You can only assign this task to an engineer on your own team.");
        }

        task.Assign(cmd.AssigneeId, cmd.ActorId);
        await _tasks.SaveChangesAsync(ct);

        // Assignment grants project access, matching UpdateTaskCommand's own assignment path.
        await _projects.AddMemberAsync(task.ProjectId, cmd.AssigneeId, ct);

        await _audit.LogAsync("TASK_ASSIGNED", cmd.ActorId, cmd.IpAddress,
            $"Task '{task.Id}' assigned to engineer '{cmd.AssigneeId}'", ct);

        if (cmd.AssigneeId != cmd.ActorId)
        {
            var taskLink = $"{_settings.AppBaseUrl}/tasks/{task.Id}";
            var body = $"""
                <p>Hi {target.Name},</p>
                <p>A task has been assigned to you: <strong>{task.Title}</strong>.</p>
                {(task.DueDate.HasValue ? $"<p>Due date: <strong>{task.DueDate.Value:D}</strong></p>" : "")}
                {EmailTemplate.Button(taskLink, "View task")}
                {EmailTemplate.Muted("This notification was sent because you were assigned to this task.")}
                """;
            await _notify.NotifyAsync(cmd.AssigneeId, NotificationKind.TaskAssigned,
                new { taskId = task.Id, taskTitle = task.Title },
                new NotificationEmail(target.Email, $"Task assigned: {task.Title}", EmailTemplate.Layout(body)), ct);
        }

        return ServiceResult<TaskDto>.Ok(TaskDto.From(task));
    }
}
