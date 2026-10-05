using System.Text.Json;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Notifications;
using Pulse.Application.Tasks;
using Pulse.Domain.Common;
using Pulse.Domain.Engineers;
using Pulse.Domain.Notifications;
using MediatR;

namespace Pulse.Application.Tasks.Commands;

/// <summary>Pulls an Active task back into the Backlog — the reverse of the auto-promotion that
/// happens on assignment (see PulseTask.ReturnToBacklog). Deliberately narrow, same reasoning as
/// AssignTaskCommand: gated to Team Lead and above rather than folded into the PM-only Edit form,
/// so a Team Lead can deprioritize and re-queue their own team's work without full task-edit access.</summary>
public record ReturnTaskToBacklogCommand(
    Guid TaskId,
    Guid ActorId,
    string? IpAddress,
    string ActorRole = "") : IRequest<ServiceResult<TaskDto>>;

public class ReturnTaskToBacklogHandler : IRequestHandler<ReturnTaskToBacklogCommand, ServiceResult<TaskDto>>
{
    private readonly ITaskRepository _tasks;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;
    private readonly IAuditLogRepository _audit;
    private readonly IProjectAccessPolicy _access;
    private readonly INotificationRepository _notifications;
    private readonly IRealtimeNotifier _realtime;

    public ReturnTaskToBacklogHandler(
        ITaskRepository tasks,
        IEngineerRepository engineers,
        ITeamRepository teams,
        IAuditLogRepository audit,
        IProjectAccessPolicy access,
        INotificationRepository notifications,
        IRealtimeNotifier realtime)
    {
        _tasks = tasks;
        _engineers = engineers;
        _teams = teams;
        _audit = audit;
        _access = access;
        _notifications = notifications;
        _realtime = realtime;
    }

    public async Task<ServiceResult<TaskDto>> Handle(ReturnTaskToBacklogCommand cmd, CancellationToken ct)
    {
        var task = await _tasks.GetByIdAsync(cmd.TaskId, ct);
        if (task is null)
            return ServiceResult<TaskDto>.Fail("NOT_FOUND", $"Task '{cmd.TaskId}' not found.");

        if (!await _access.CanAccessTaskAsync(cmd.TaskId, cmd.ActorId, cmd.ActorRole, ct))
            return ServiceResult<TaskDto>.Fail("FORBIDDEN", "You do not have access to this task.");

        // A plain Team Lead may only pull back work currently held by an engineer on the team they
        // lead — every other role this endpoint admits (PM-or-above) is unrestricted, same
        // reasoning as AssignTaskCommand's identical restriction.
        if (cmd.ActorRole == Roles.TeamLead && task.AssigneeId.HasValue)
        {
            var currentAssignee = await _engineers.GetByIdAsync(task.AssigneeId.Value, ct);
            var ledTeam = await DepartmentScope.GetLedTeamAsync(cmd.ActorId, _teams, ct);
            if (ledTeam is null)
                return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION", "You do not lead any team.");
            if (currentAssignee?.TeamId != ledTeam.Id)
                return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION",
                    "You can only return a task currently held by an engineer on your own team.");
        }

        var previousAssigneeId = task.AssigneeId;

        try
        {
            task.ReturnToBacklog(cmd.ActorId);
        }
        catch (DomainException ex)
        {
            return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION", ex.Message);
        }

        await _tasks.SaveChangesAsync(ct);

        await _audit.LogAsync("TASK_RETURNED_TO_BACKLOG", cmd.ActorId, cmd.IpAddress,
            $"Task '{task.Id}' returned to backlog (was assigned to '{previousAssigneeId}')", ct);

        if (previousAssigneeId.HasValue && previousAssigneeId.Value != cmd.ActorId)
        {
            var n = Notification.Create(previousAssigneeId.Value, NotificationKind.TaskReturnedToBacklog,
                JsonSerializer.Serialize(new { taskId = task.Id, taskTitle = task.Title }));
            await _notifications.AddAsync(n, ct);
            await _notifications.SaveChangesAsync(ct);
            await _realtime.SendNotificationAsync(previousAssigneeId.Value, NotificationDto.From(n), ct);
        }

        return ServiceResult<TaskDto>.Ok(TaskDto.From(task));
    }
}
