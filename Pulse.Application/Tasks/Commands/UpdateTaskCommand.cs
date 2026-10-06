using System.Text.Json;
using Pulse.Application.Auth;
using Pulse.Application.CheckIns;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Epics;
using Pulse.Application.Notifications;
using Pulse.Application.Tasks;
using Pulse.Domain.Common;
using Pulse.Domain.Notifications;
using Pulse.Domain.Tasks;
using MediatR;

namespace Pulse.Application.Tasks.Commands;

public record UpdateTaskCommand(
    Guid TaskId,
    string? Title,
    string? Description,
    string? AcceptanceCriteria,
    BugSeverity? Severity,
    Guid? EpicId,
    bool? RemoveFromEpic,
    int? Points,
    DateOnly? DueDate,
    DateOnly? ActualEndDate,
    Guid? AssigneeId,
    TaskType? Type,
    bool? MarkDone,
    Guid? SprintId,
    bool? RemoveFromSprint,
    string? Status,
    string? BlockerReason,
    string? PauseNote,
    bool? RequiresQa,
    Discipline? Discipline,
    Guid ActorId,
    string? IpAddress,
    string ActorRole = "",
    int? Priority = null,
    bool? RemovePriority = null,
    string? ExternalReference = null,
    bool? RemoveExternalReference = null,
    bool? RequiresFrontendHandoff = null,
    bool? RequiresPrApproval = null,
    string? DueDateChangeReason = null,
    string? PointsChangeReason = null) : IRequest<ServiceResult<TaskDto>>;

public class UpdateTaskHandler : IRequestHandler<UpdateTaskCommand, ServiceResult<TaskDto>>
{
    private readonly ITaskRepository _tasks;
    private readonly IEngineerRepository _engineers;
    private readonly IAuditLogRepository _audit;
    private readonly IEscalationEventRepository _escalationEvents;
    private readonly IEpicRepository _epics;
    private readonly ITaskDependencyRepository _dependencies;
    private readonly IAppSettings _settings;
    private readonly IProjectAccessPolicy _access;
    private readonly IProjectRepository _projects;
    private readonly ISprintRepository _sprints;
    private readonly ICheckInRepository _checkIns;
    private readonly ITeamRepository _teams;
    private readonly INotificationDispatcher _notify;

    public UpdateTaskHandler(
        ITaskRepository tasks,
        IEngineerRepository engineers,
        IAuditLogRepository audit,
        IEscalationEventRepository escalationEvents,
        IEpicRepository epics,
        ITaskDependencyRepository dependencies,
        IAppSettings settings,
        IProjectAccessPolicy access,
        IProjectRepository projects,
        ISprintRepository sprints,
        ICheckInRepository checkIns,
        ITeamRepository teams,
        INotificationDispatcher notify)
    {
        _notify = notify;
        _tasks = tasks;
        _engineers = engineers;
        _audit = audit;
        _escalationEvents = escalationEvents;
        _epics = epics;
        _dependencies = dependencies;
        _settings = settings;
        _access = access;
        _projects = projects;
        _sprints = sprints;
        _checkIns = checkIns;
        _teams = teams;
    }

    public async Task<ServiceResult<TaskDto>> Handle(UpdateTaskCommand cmd, CancellationToken ct)
    {
        var task = await _tasks.GetByIdAsync(cmd.TaskId, ct);
        if (task is null)
            return ServiceResult<TaskDto>.Fail("NOT_FOUND", $"Task '{cmd.TaskId}' not found.");

        var allowed = await _access.CanAccessTaskAsync(cmd.TaskId, cmd.ActorId, cmd.ActorRole, ct);
        if (!allowed)
            return ServiceResult<TaskDto>.Fail("FORBIDDEN", "You do not have access to this task.");

        // Below Team Lead, an actor may only ever assign a task to themselves — mirrors
        // CreateTaskCommand's same restriction. In practice this branch is unreachable through the
        // real API today: PATCH /tasks/{id} is already gated to TeamLeadOrAbove at the controller,
        // and this is the command's only caller — but that pairing is exactly the kind of thing
        // that silently breaks the moment either side changes, so this stays as defense-in-depth
        // rather than trusting the controller gate alone.
        var assigneeId = cmd.AssigneeId;
        if (assigneeId.HasValue && assigneeId.Value != cmd.ActorId
            && !CapabilityRegistry.All[CapabilityRegistry.TeamLeadOrAbove].AllowedRoles.Contains(cmd.ActorRole))
            assigneeId = cmd.ActorId;

        var effectiveDueDate = cmd.DueDate ?? task.DueDate;
        var dueDateChanged = cmd.DueDate.HasValue && cmd.DueDate != task.DueDate;
        var reassigned = assigneeId.HasValue && assigneeId.Value != task.AssigneeId;
        var epicIdBefore = task.EpicId;
        PulseTask? qaAcceptedParent = null;
        var wasMarkedDone = false;
        Pulse.Domain.Engineers.Engineer? newAssignee = null;

        if (cmd.SprintId.HasValue)
        {
            var sprint = await _sprints.GetByIdAsync(cmd.SprintId.Value, ct);
            if (sprint is null)
                return ServiceResult<TaskDto>.Fail("NOT_FOUND", $"Sprint '{cmd.SprintId}' not found.");

            // Auto-suggest: default the due date to the sprint's end date rather than
            // requiring one up front — still editable via an explicit cmd.DueDate.
            if (effectiveDueDate is null)
            {
                effectiveDueDate = sprint.EndDate;
                dueDateChanged = true;
            }
        }

        // A due date is required once this edit gives the task enough shape to be scheduled —
        // an assignee, real points, or a priority. Mirrors CreateTaskCommand's rule. Gated on
        // what THIS call is setting (cmd.*, not the task's current state) so an unrelated edit to
        // an already-noncompliant legacy task (e.g. one pointed before this rule existed) isn't
        // retroactively blocked.
        if ((assigneeId.HasValue || cmd.Points is > 0 || cmd.Priority.HasValue) && effectiveDueDate is null)
            return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION",
                "A due date is required once an assignee, points, or priority is set.");

        // Moving an EXISTING due date needs a stated reason. Setting the first date on an
        // unscheduled task (or the sprint auto-fill above) does not. A done/in-QA task's due date is
        // never touched (UpdateDetails is skipped below), so it isn't asked either.
        var dueDateReason = cmd.DueDateChangeReason?.Trim();
        if (cmd.DueDate.HasValue && task.DueDate.HasValue && cmd.DueDate != task.DueDate
            && task.Status is not (Domain.Tasks.TaskStatus.Done or Domain.Tasks.TaskStatus.InQa))
        {
            if (string.IsNullOrEmpty(dueDateReason))
                return ServiceResult<TaskDto>.Fail("VALIDATION_ERROR", "A reason is required when changing the due date.");
            if (dueDateReason.Length > 500)
                return ServiceResult<TaskDto>.Fail("VALIDATION_ERROR", "The due date change reason must be 500 characters or fewer.");
        }

        // Changing an EXISTING estimate once work has begun needs a stated reason: points feed velocity, workload
        // and the overwork thresholds, so moving them after the fact must be explained. The first estimate on an
        // unpointed task, and any change while the task is still in Backlog, are not asked. (Planning Poker
        // re-estimates have their own approval step and are recorded there.)
        var pointsReason = cmd.PointsChangeReason?.Trim();
        if (cmd.Points.HasValue && task.Points > 0 && cmd.Points != task.Points
            && task.Status is not (Domain.Tasks.TaskStatus.Backlog or Domain.Tasks.TaskStatus.Done or Domain.Tasks.TaskStatus.InQa))
        {
            if (string.IsNullOrEmpty(pointsReason))
                return ServiceResult<TaskDto>.Fail("VALIDATION_ERROR", "A reason is required when changing the points on a task that has been started.");
            if (pointsReason.Length > 500)
                return ServiceResult<TaskDto>.Fail("VALIDATION_ERROR", "The points change reason must be 500 characters or fewer.");
        }

        try
        {
            // A done/in-QA task can't have its title, points, due date, or acceptance criteria
            // edited (UpdateDetails enforces that) — but other fields below (RequiresQa in
            // particular, for correcting a mistake after the fact) should still go through.
            var detailsChanged = cmd.Title is not null || cmd.Points.HasValue || effectiveDueDate != task.DueDate || cmd.AcceptanceCriteria is not null;
            if (detailsChanged && task.Status is not (Domain.Tasks.TaskStatus.Done or Domain.Tasks.TaskStatus.InQa))
            {
                task.UpdateDetails(
                    cmd.Title ?? task.Title,
                    cmd.Description is not null ? DescriptionSanitizer.Sanitize(cmd.Description) : task.Description,
                    cmd.AcceptanceCriteria is not null ? DescriptionSanitizer.Sanitize(cmd.AcceptanceCriteria) : task.AcceptanceCriteria,
                    cmd.Points ?? task.Points,
                    effectiveDueDate,
                    cmd.ActorId,
                    dueDateReason,
                    pointsReason);
            }

            if (assigneeId.HasValue)
            {
                newAssignee = await _engineers.GetByIdAsync(assigneeId.Value, ct);
                if (newAssignee is null || !newAssignee.IsActive)
                    return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION", "Assignee not found or inactive.");

                var qaCheck = await QaAssignmentPolicy.ValidateAsync(task, newAssignee, cmd.ActorRole, _tasks, _teams, ct);
                if (!qaCheck.IsAllowed)
                    return ServiceResult<TaskDto>.Fail(qaCheck.ErrorCode!, qaCheck.ErrorMessage!);

                task.Assign(assigneeId.Value, cmd.ActorId);
            }

            if (cmd.Type.HasValue)
                task.SetType(cmd.Type.Value, cmd.ActorId);

            if (cmd.Severity.HasValue || (cmd.Type.HasValue && cmd.Type.Value != TaskType.Bug))
                task.SetSeverity(cmd.Type.HasValue && cmd.Type.Value != TaskType.Bug ? null : cmd.Severity);

            if (cmd.MarkDone == true || cmd.Status?.ToLower() == "done")
            {
                task.MarkDone(cmd.ActorId);
                wasMarkedDone = true;
                await AutoCheckIn.EnsureForTaskCompletionAsync(_checkIns, cmd.ActorId, task.ProjectId, task.Title, ct);
                // QA task accepted — also complete the original (parent) task.
                if (task.ParentTaskId.HasValue)
                {
                    var parentTask = await _tasks.GetByIdAsync(task.ParentTaskId.Value, ct);
                    if (parentTask?.Status == Domain.Tasks.TaskStatus.InQa)
                    {
                        parentTask.AcceptQa(cmd.ActorId);
                        qaAcceptedParent = parentTask;
                        await _audit.LogAsync("QA_ACCEPTED", cmd.ActorId, cmd.IpAddress,
                            $"QA accepted for task '{parentTask.Id}' via QA task '{task.Id}'.", ct);
                    }
                }
            }
            else if (cmd.Status?.ToLower() == "blocked")
                task.FlagBlocker(cmd.BlockerReason ?? "Blocker flagged", cmd.ActorId);
            else if (cmd.Status?.ToLower() == "paused")
                task.Pause(cmd.PauseNote, cmd.ActorId);
            else if (cmd.Status?.ToLower() == "active")
            {
                if (task.Status == Domain.Tasks.TaskStatus.Blocked)
                    task.ClearBlocker(cmd.ActorId);
                else if (task.Status == Domain.Tasks.TaskStatus.Paused)
                    task.Resume(cmd.ActorId);
                else if (task.Status is Domain.Tasks.TaskStatus.Done or Domain.Tasks.TaskStatus.InQa)
                    throw new DomainException("Cannot reactivate a completed or in-QA task directly. Use the QA reject flow.");
            }

            if (cmd.RequiresQa.HasValue)
                task.SetRequiresQa(cmd.RequiresQa.Value);

            if (cmd.RequiresFrontendHandoff.HasValue)
                task.SetRequiresFrontendHandoff(cmd.RequiresFrontendHandoff.Value);

            if (cmd.RequiresPrApproval.HasValue)
                task.SetRequiresPrApproval(cmd.RequiresPrApproval.Value);

            if (cmd.Discipline.HasValue)
                task.SetDiscipline(cmd.Discipline.Value);

            if (cmd.RemovePriority == true)
                task.SetPriority(null);
            else if (cmd.Priority.HasValue)
                task.SetPriority(cmd.Priority.Value);

            if (cmd.RemoveExternalReference == true)
                task.SetExternalReference(null);
            else if (cmd.ExternalReference is not null)
                task.SetExternalReference(cmd.ExternalReference.Trim());

            if (cmd.ActualEndDate.HasValue)
                task.SetActualEndDate(cmd.ActualEndDate.Value);

            if (cmd.RemoveFromEpic == true)
                task.AssignToEpic(null);
            else if (cmd.EpicId.HasValue)
                task.AssignToEpic(cmd.EpicId.Value);

            if (cmd.RemoveFromSprint == true)
                task.RemoveFromSprint();
            else if (cmd.SprintId.HasValue)
                task.AssignToSprint(cmd.SprintId.Value);
        }
        catch (DomainException ex)
        {
            return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION", ex.Message);
        }

        await _tasks.SaveChangesAsync(ct);

        // Assignment grants project access: ensure the assignee is a project member
        // (idempotent, additive — the previous assignee keeps their membership).
        if (assigneeId.HasValue)
            await _projects.AddMemberAsync(task.ProjectId, assigneeId.Value, ct);

        // Roll up epic status — handle both old epic (if task was moved out) and current epic.
        if (epicIdBefore.HasValue && epicIdBefore != task.EpicId)
            await EpicStatusRollUp.ApplyAsync(epicIdBefore, _tasks, _epics, ct);
        await EpicStatusRollUp.ApplyAsync(task.EpicId, _tasks, _epics, ct);

        // Re-arm escalation events so the scanner fires on the new schedule.
        if (dueDateChanged || reassigned)
            await _escalationEvents.ClearForTaskAsync(task.Id, ct);

        await _audit.LogAsync("TASK_UPDATED", cmd.ActorId, cmd.IpAddress,
            $"Updated task {task.Id} '{task.Title}'", ct);

        if (reassigned)
            await _audit.LogAsync("TASK_REASSIGNED", cmd.ActorId, cmd.IpAddress,
                $"Task '{task.Id}' reassigned to engineer '{assigneeId}'", ct);

        // ── Notifications ──────────────────────────────────────────────────────

        // QA accepted — notify the original task's engineer, in-app and by email (mirrors the
        // QA-rejected path, which already does both — approval shouldn't be a lesser notification
        // than rejection).
        if (qaAcceptedParent?.AssigneeId is Guid qaParentAssignee)
        {
            await PushAsync(qaParentAssignee, NotificationKind.QaAccepted,
                new { taskId = qaAcceptedParent.Id, taskTitle = qaAcceptedParent.Title }, ct);

            var qaParentEngineer = await _engineers.GetByIdAsync(qaParentAssignee, ct);
            if (qaParentEngineer is not null)
            {
                var taskLink = $"{_settings.AppBaseUrl}/tasks/{qaAcceptedParent.Id}";
                var body = $"""
                    <p>Hi {qaParentEngineer.Name},</p>
                    <p>Your work passed QA review: <strong>{qaAcceptedParent.Title}</strong>.</p>
                    {EmailTemplate.Button(taskLink, "View task")}
                    {EmailTemplate.Muted("This notification was sent because you are the assignee of this task.")}
                    """;
                await _notify.EmailAsync(qaParentEngineer.Id, NotificationKind.QaAccepted, new NotificationEmail(qaParentEngineer.Email, $"QA passed: {qaAcceptedParent.Title}", EmailTemplate.Layout(body)), ct);
            }
        }

        // Task marked done — notify assignees of dependent tasks that are now fully unblocked.
        if (wasMarkedDone)
        {
            var dependents = await _dependencies.GetDependentsAsync(task.Id, ct);
            foreach (var dep in dependents)
            {
                var depTask = await _tasks.GetByIdAsync(dep.DependentTaskId, ct);
                if (depTask?.AssigneeId is null || depTask.Status == Domain.Tasks.TaskStatus.Done)
                    continue;

                var remainingBlockers = await _dependencies.GetBlockingAsync(dep.DependentTaskId, ct);
                var fullyUnblocked = true;
                foreach (var b in remainingBlockers)
                {
                    if (b.BlockingTaskId == task.Id) continue; // just completed — ignore
                    var bTask = await _tasks.GetByIdAsync(b.BlockingTaskId, ct);
                    if (bTask?.Status != Domain.Tasks.TaskStatus.Done) { fullyUnblocked = false; break; }
                }

                if (fullyUnblocked)
                    await PushAsync(depTask.AssigneeId.Value, NotificationKind.TaskUnblocked,
                        new { taskId = depTask.Id, taskTitle = depTask.Title }, ct);
            }
        }

        // Task assigned by someone else — notify the new assignee in-app and by email.
        if (assigneeId.HasValue && assigneeId.Value != cmd.ActorId)
        {
            await PushAsync(assigneeId.Value, NotificationKind.TaskAssigned,
                new { taskId = task.Id, taskTitle = task.Title }, ct);

            if (newAssignee is not null)
            {
                var taskLink = $"{_settings.AppBaseUrl}/tasks/{task.Id}";
                var body = $"""
                    <p>Hi {newAssignee.Name},</p>
                    <p>A task has been assigned to you: <strong>{task.Title}</strong>.</p>
                    {(task.DueDate.HasValue ? $"<p>Due date: <strong>{task.DueDate.Value:D}</strong></p>" : "")}
                    {EmailTemplate.Button(taskLink, "View task")}
                    {EmailTemplate.Muted("This notification was sent because you were assigned to this task.")}
                    """;
                await _notify.EmailAsync(newAssignee.Id, NotificationKind.TaskAssigned, new NotificationEmail(newAssignee.Email, $"Task assigned: {task.Title}", EmailTemplate.Layout(body)), ct);
            }
        }

        return ServiceResult<TaskDto>.Ok(TaskDto.From(task));
    }

    private Task PushAsync(Guid userId, string kind, object payload, CancellationToken ct) =>
        _notify.NotifyAsync(userId, kind, payload, ct: ct);
}
