using System.Text.Json;
using Pulse.Application.Auth;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Notifications;
using Pulse.Application.Projects;
using Pulse.Application.Tasks;
using Pulse.Domain.Notifications;
using Pulse.Domain.Projects;
using Pulse.Domain.Tasks;
using MediatR;

namespace Pulse.Application.Tasks.Commands;

public record CreateTaskCommand(
    string Title,
    string? Description,
    int? Points,
    DateOnly? DueDate,
    Guid ProjectId,
    Guid? AssigneeId,
    TaskType Type,
    Guid? EpicId,
    bool RequiresQa,
    Discipline? Discipline,
    Guid? ParentTaskId,
    Guid ActorId,
    string? IpAddress,
    string ActorRole = "",
    int? Priority = null,
    string? AcceptanceCriteria = null,
    string? ExternalReference = null,
    bool RequiresFrontendHandoff = false,
    bool Personal = false) : IRequest<ServiceResult<TaskDto>>;

public class CreateTaskHandler : IRequestHandler<CreateTaskCommand, ServiceResult<TaskDto>>
{
    private readonly ITaskRepository _tasks;
    private readonly IProjectRepository _projects;
    private readonly IEngineerRepository _engineers;
    private readonly IAuditLogRepository _audit;
    private readonly IProjectAccessPolicy _access;
    private readonly INotificationRepository _notifications;
    private readonly IRealtimeNotifier _realtime;
    private readonly IEmailQueue _emailQueue;
    private readonly IAppSettings _settings;

    public CreateTaskHandler(
        ITaskRepository tasks,
        IProjectRepository projects,
        IEngineerRepository engineers,
        IAuditLogRepository audit,
        IProjectAccessPolicy access,
        INotificationRepository notifications,
        IRealtimeNotifier realtime,
        IEmailQueue emailQueue,
        IAppSettings settings)
    {
        _tasks = tasks;
        _projects = projects;
        _engineers = engineers;
        _audit = audit;
        _access = access;
        _notifications = notifications;
        _realtime = realtime;
        _emailQueue = emailQueue;
        _settings = settings;
    }

    /// <summary>The caller's personal project, created (with them as its only member) the first time
    /// they make a personal task. Code is "P" + their initials, made unique like any project code.</summary>
    private async Task<Project?> GetOrCreatePersonalProjectAsync(Guid ownerId, CancellationToken ct)
    {
        var existing = await _projects.GetPersonalProjectAsync(ownerId, ct);
        if (existing is not null) return existing;

        var owner = await _engineers.GetByIdAsync(ownerId, ct);
        if (owner is null) return null;

        var initials = new string(owner.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0])).Where(char.IsLetterOrDigit).Take(3).ToArray());
        var code = ProjectCodeGenerator.MakeUnique($"P{(initials.Length == 0 ? "X" : initials)}",
            new HashSet<string>(await _projects.GetAllCodesAsync(ct)));

        var project = Project.CreatePersonal(ownerId, owner.Name, code);
        await _projects.AddAsync(project, ct);
        await _projects.SaveChangesAsync(ct);
        // Membership is what lets the owner (and nobody else) work in it — see ProjectAccessPolicy.
        await _projects.AddMemberAsync(project.Id, ownerId, ct);
        return project;
    }

    public async Task<ServiceResult<TaskDto>> Handle(CreateTaskCommand cmd, CancellationToken ct)
    {
        var isPersonal = cmd.Personal;
        if (isPersonal)
        {
            // A personal task is a private to-do in the caller's own personal project, so it can be
            // assigned to them and logged against without belonging to any real project. Everything
            // that makes no sense for one (another assignee, estimate, epic, QA, hand-off) is dropped.
            if (!CapabilityRegistry.All[CapabilityRegistry.PersonalTaskCreator].AllowedRoles.Contains(cmd.ActorRole))
                return ServiceResult<TaskDto>.Fail("FORBIDDEN", "Personal tasks are available to HR and Accountant.");

            var personal = await GetOrCreatePersonalProjectAsync(cmd.ActorId, ct);
            if (personal is null)
                return ServiceResult<TaskDto>.Fail("NOT_FOUND", "Engineer not found.");

            cmd = cmd with
            {
                ProjectId = personal.Id, AssigneeId = cmd.ActorId, Points = null, EpicId = null,
                ParentTaskId = null, RequiresQa = false, RequiresFrontendHandoff = false, Discipline = null,
                Personal = false,
            };
        }

        // Below Team Lead, an actor may only ever create a task assigned to themselves — the
        // create-page UI already locks the assignee picker to "Assign to me" for this role tier,
        // but that's a UI convenience, not a guarantee; a submitted assignee that isn't the actor
        // is silently corrected here rather than rejected, mirroring how Points is clamped below.
        var assigneeId = cmd.AssigneeId;
        if (assigneeId.HasValue && assigneeId.Value != cmd.ActorId
            && !CapabilityRegistry.All[CapabilityRegistry.TeamLeadOrAbove].AllowedRoles.Contains(cmd.ActorRole))
            assigneeId = cmd.ActorId;

        if (assigneeId.HasValue)
        {
            var engineer = await _engineers.GetByIdAsync(assigneeId.Value, ct);
            if (engineer is null || !engineer.IsActive)
                return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION", "Assignee not found or inactive.");
        }

        // Points is optional — a task can be created ungroomed (0 points, the same sentinel
        // PromoteFromBacklogIfGroomed/SendToQa/MarkDone/AssignToSprint already treat as "not yet
        // estimated") and pointed later via Planning Poker or a quick edit, rather than forcing
        // whoever files it to guess a number up front. Below Team Lead, a submitted estimate is
        // dropped rather than rejected — Engineer/Designer can still file the task, just without
        // an estimate; a lead points it once they pick it up, same as the "estimate later" path.
        var points = cmd.Points ?? 0;
        if (points != 0 && !CapabilityRegistry.All[CapabilityRegistry.TeamLeadOrAbove].AllowedRoles.Contains(cmd.ActorRole))
            points = 0;

        var taskNumber = await _tasks.GetNextTaskNumberAsync(cmd.ProjectId, ct);

        var input = new TaskCreationInput(
            cmd.Title,
            cmd.Description is not null ? DescriptionSanitizer.Sanitize(cmd.Description) : null,
            cmd.AcceptanceCriteria is not null ? DescriptionSanitizer.Sanitize(cmd.AcceptanceCriteria) : null,
            points, cmd.DueDate, cmd.ProjectId, assigneeId, cmd.Type, cmd.EpicId,
            cmd.RequiresQa, cmd.Discipline, cmd.ParentTaskId, cmd.Priority, cmd.ActorId, cmd.ActorRole,
            taskNumber, cmd.ExternalReference?.Trim(), cmd.RequiresFrontendHandoff);

        var built = await TaskCreationPolicy.ValidateAndBuildAsync(input, _projects, _access, ct);
        if (!built.IsSuccess)
            return ServiceResult<TaskDto>.Fail(built.ErrorCode!, built.ErrorMessage!);

        var task = built.Data!;
        if (isPersonal)
            task.ActivateAsPersonal(cmd.ActorId);
        await _tasks.AddAsync(task, ct);
        await _tasks.SaveChangesAsync(ct);

        // Assignment grants project access: ensure the assignee is a project member
        // (idempotent). Membership is additive — it is never removed on reassignment.
        if (assigneeId.HasValue)
            await _projects.AddMemberAsync(cmd.ProjectId, assigneeId.Value, ct);

        await _audit.LogAsync("TASK_CREATED", cmd.ActorId, cmd.IpAddress,
            $"Created task {task.Id} '{task.Title}' in project {cmd.ProjectId}", ct);

        var creator = await _engineers.GetByIdAsync(cmd.ActorId, ct);

        // Assigned at creation by someone else — notify in-app and by email, same as reassigning
        // an existing task already does (UpdateTaskCommand). A brand-new task landing in someone's
        // queue shouldn't be silent just because it happened at creation instead of a later edit.
        if (assigneeId.HasValue && assigneeId.Value != cmd.ActorId)
        {
            var n = Notification.Create(assigneeId.Value, NotificationKind.TaskAssigned,
                JsonSerializer.Serialize(new { taskId = task.Id, taskTitle = task.Title }), NotificationChannel.InApp);
            await _notifications.AddAsync(n, ct);
            await _notifications.SaveChangesAsync(ct);
            await _realtime.SendNotificationAsync(assigneeId.Value, NotificationDto.From(n), ct);

            var newAssignee = await _engineers.GetByIdAsync(assigneeId.Value, ct);
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
                _emailQueue.Enqueue(newAssignee.Email, $"Task assigned: {task.Title}", EmailTemplate.Layout(body));
            }
        }

        return ServiceResult<TaskDto>.Ok(TaskDto.From(task, creatorName: creator?.Name));
    }
}
