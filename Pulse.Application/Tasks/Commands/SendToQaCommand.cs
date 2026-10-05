using System.Text.Json;
using Pulse.Application.CheckIns;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Notifications;
using Pulse.Application.Overwork;
using Pulse.Domain.Common;
using Pulse.Domain.Engineers;
using Pulse.Domain.Notifications;
using Pulse.Domain.Tasks;
using MediatR;

namespace Pulse.Application.Tasks.Commands;

/// <param name="QaEngineerId">Optional explicit reviewer, from the "Send to QA" picker. When omitted,
/// falls back to the automatic tiered pick (see FindQaEngineerAsync) — unchanged default behavior for
/// any caller that doesn't supply one.</param>
public record SendToQaCommand(Guid TaskId, Guid ActorId, string? IpAddress, string ActorRole = "", Guid? QaEngineerId = null) : IRequest<ServiceResult<TaskDto>>;

public class SendToQaHandler : IRequestHandler<SendToQaCommand, ServiceResult<TaskDto>>
{
    private readonly ITaskRepository _tasks;
    private readonly IEngineerRepository _engineers;
    private readonly IProjectRepository _projects;
    private readonly ITeamRepository _teams;
    private readonly IAuditLogRepository _audit;
    private readonly IProjectAccessPolicy _access;
    private readonly OverworkThresholds _thresholds;
    private readonly INotificationRepository _notifications;
    private readonly IRealtimeNotifier _realtime;
    private readonly IEmailQueue _emailQueue;
    private readonly IAppSettings _settings;
    private readonly ICheckInRepository _checkIns;

    public SendToQaHandler(
        ITaskRepository tasks,
        IEngineerRepository engineers,
        IProjectRepository projects,
        ITeamRepository teams,
        IAuditLogRepository audit,
        IProjectAccessPolicy access,
        OverworkThresholds thresholds,
        INotificationRepository notifications,
        IRealtimeNotifier realtime,
        IEmailQueue emailQueue,
        IAppSettings settings,
        ICheckInRepository checkIns)
    {
        _tasks = tasks;
        _engineers = engineers;
        _projects = projects;
        _teams = teams;
        _audit = audit;
        _access = access;
        _thresholds = thresholds;
        _notifications = notifications;
        _realtime = realtime;
        _emailQueue = emailQueue;
        _settings = settings;
        _checkIns = checkIns;
    }

    public async Task<ServiceResult<TaskDto>> Handle(SendToQaCommand cmd, CancellationToken ct)
    {
        var task = await _tasks.GetByIdAsync(cmd.TaskId, ct);
        if (task is null)
            return ServiceResult<TaskDto>.Fail("NOT_FOUND", $"Task '{cmd.TaskId}' not found.");

        if (task.AssigneeId != cmd.ActorId
            && !await _access.CanAccessProjectAsync(task.ProjectId, cmd.ActorId, cmd.ActorRole, ct))
            return ServiceResult<TaskDto>.Fail("FORBIDDEN", "You do not have access to this task.");

        if (task.QaTaskId.HasValue)
            return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION",
                "A QA task already exists for this task.");

        // QA gets its own due date — a configurable lead time from today, independent of the
        // original task's due date (which may already be past, or far in the future).
        var qaDueDate = BusinessDays.Add(DateOnly.FromDateTime(DateTime.UtcNow), _thresholds.QaLeadTimeDays);
        PulseTask qaTask;

        // The engineer who built it, not necessarily whoever sent it (PMO can send on their behalf).
        var sendingEngineerId = task.AssigneeId ?? cmd.ActorId;

        try
        {
            task.SendToQa(cmd.ActorId);

            // Auto-create the linked QA task (same sprint/project/epic). Copies task.Points, so
            // if the original was already sprint-assigned but later edited down to 0 points,
            // AssignToSprint below throws just like it would for the original task. TaskType is
            // always Review here, regardless of the original task's own type (Feature, Bug, ...)
            // — this card represents reviewing already-built work, not new work of that type.
            qaTask = PulseTask.Create(
                $"[QA] {task.Title}",
                task.Points,
                task.ProjectId,
                TaskType.Review,
                qaDueDate,
                cmd.ActorId);

            qaTask.AssignTaskNumber(await _tasks.GetNextTaskNumberAsync(task.ProjectId, ct));

            if (task.EpicId.HasValue)
                qaTask.AssignToEpic(task.EpicId);

            if (task.SprintId.HasValue)
                qaTask.AssignToSprint(task.SprintId.Value);
        }
        catch (DomainException ex)
        {
            return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION", ex.Message);
        }

        qaTask.SetParentTaskId(task.Id);

        Guid? qaEngineer;
        if (cmd.QaEngineerId.HasValue)
        {
            // Explicit pick from the "Send to QA" picker — validated with the same shared policy
            // every other manual QA (re)assignment path already goes through (UpdateTaskHandler,
            // BulkReassignCommand, LoanTaskCommand), so a discipline-less task still requires the
            // right department-head role and target department here too.
            var targetEngineer = await _engineers.GetByIdAsync(cmd.QaEngineerId.Value, ct);
            if (targetEngineer is null || !targetEngineer.IsActive)
                return ServiceResult<TaskDto>.Fail("VALIDATION_ERROR", "Selected QA engineer is not available.");

            var check = await QaAssignmentPolicy.ValidateAsync(qaTask, targetEngineer, cmd.ActorRole, _tasks, _teams, ct);
            if (!check.IsAllowed)
                return ServiceResult<TaskDto>.Fail(check.ErrorCode!, check.ErrorMessage!);

            var members = await _projects.ListMembersAsync(task.ProjectId, ct);
            if (!members.Any(m => m.EngineerId == targetEngineer.Id))
                await _projects.AddMemberAsync(task.ProjectId, targetEngineer.Id, ct);

            qaEngineer = targetEngineer.Id;
        }
        else
        {
            qaEngineer = await FindQaEngineerAsync(task.ProjectId, task.Discipline, ct);
        }

        if (qaEngineer.HasValue)
            qaTask.Assign(qaEngineer.Value, cmd.ActorId);

        await _tasks.AddAsync(qaTask, ct);

        task.SetQaTaskId(qaTask.Id);

        await _tasks.SaveChangesAsync(ct);

        await AutoCheckIn.EnsureForSentToQaAsync(_checkIns, sendingEngineerId, task.ProjectId, task.Title, ct);

        await _audit.LogAsync("TASK_SENT_TO_QA", cmd.ActorId, cmd.IpAddress,
            $"Task '{task.Id}' sent to QA. QA task '{qaTask.Id}' created{(qaEngineer.HasValue ? $", assigned to '{qaEngineer.Value}'" : string.Empty)}.", ct);

        if (qaEngineer.HasValue)
        {
            // The QA engineer was just handed a review to do — until now this was silent, so
            // they'd only find out by happening to check the board or their task list. Reuses
            // the same in-app kind UpdateTaskCommand's general assignment path uses (the
            // frontend already renders it, and the "[QA] " title prefix makes the review nature
            // clear at a glance); the email copy is written specifically for a QA review though.
            var payload = JsonSerializer.Serialize(new { taskId = qaTask.Id, taskTitle = qaTask.Title });
            var n = Notification.Create(qaEngineer.Value, NotificationKind.TaskAssigned, payload, NotificationChannel.InApp);
            await _notifications.AddAsync(n, ct);
            await _notifications.SaveChangesAsync(ct);
            await _realtime.SendNotificationAsync(qaEngineer.Value, NotificationDto.From(n), ct);

            var reviewer = await _engineers.GetByIdAsync(qaEngineer.Value, ct);
            if (reviewer is not null)
            {
                var taskLink = $"{_settings.AppBaseUrl}/tasks/{qaTask.Id}";
                var body = $"""
                    <p>Hi {reviewer.Name},</p>
                    <p>You've been assigned a QA review: <strong>{task.Title}</strong>.</p>
                    {(qaTask.DueDate.HasValue ? $"<p>Due date: <strong>{qaTask.DueDate.Value:D}</strong></p>" : "")}
                    {EmailTemplate.Button(taskLink, "View task")}
                    {EmailTemplate.Muted("This notification was sent because you were assigned to review this task.")}
                    """;
                _emailQueue.Enqueue(reviewer.Email, $"QA review assigned: {task.Title}", EmailTemplate.Layout(body));
            }
        }
        // No QA engineer matched the project — the task is otherwise correctly "awaiting QA",
        // but nobody was actually notified to review it. The original assignee is allowed to
        // accept their own unassigned QA task, so let them know that option exists rather than
        // leaving the QA task to be found only by browsing the project's backlog filter.
        else if (task.AssigneeId is Guid submitterId)
        {
            // taskId points at the QA task itself, not the original — clicking the notification
            // should land on the thing the submitter can actually act on (accept their own QA task).
            var payload = JsonSerializer.Serialize(new
            {
                taskId    = qaTask.Id,
                taskTitle = qaTask.Title,
            });

            var n = Notification.Create(submitterId, NotificationKind.QaUnassigned, payload, NotificationChannel.InApp);
            await _notifications.AddAsync(n, ct);
            await _notifications.SaveChangesAsync(ct);
            await _realtime.SendNotificationAsync(submitterId, NotificationDto.From(n), ct);
        }

        return ServiceResult<TaskDto>.Ok(TaskDto.From(task));
    }

    /// <summary>Automatic fallback used when the caller submits no explicit QaEngineerId. Delegates
    /// the actual tier logic to QaAutoAssignment.Recommend (shared with GetQaSendCandidatesQuery's
    /// "recommended" hint) and only owns the side effect that logic can't itself do: granting the
    /// picked engineer project access — same as a Loan would — when they weren't already a member.</summary>
    private async Task<Guid?> FindQaEngineerAsync(Guid projectId, Discipline? discipline, CancellationToken ct)
    {
        var members = await _projects.ListMembersAsync(projectId, ct);
        var memberIdList = members.Select(m => m.EngineerId).ToList();
        var memberIds = memberIdList.ToHashSet();
        var projectEngineers = memberIdList.Count > 0
            ? await _engineers.GetByIdsAsync(memberIdList, ct)
            : [];

        var qaEngineersOnProject = projectEngineers.Where(e => e.IsQa && e.IsActive).ToList();
        var allActiveQa = (await _engineers.ListActiveAsync(ct)).Where(e => e.IsQa).ToList();

        var recommended = QaAutoAssignment.Recommend(qaEngineersOnProject, allActiveQa, discipline);
        if (recommended.HasValue && !memberIds.Contains(recommended.Value))
            await _projects.AddMemberAsync(projectId, recommended.Value, ct);

        return recommended;
    }
}
