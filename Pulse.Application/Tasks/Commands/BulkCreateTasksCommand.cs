using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Tasks;
using MediatR;

namespace Pulse.Application.Tasks.Commands;

public record BulkTaskItem(
    string Title,
    string? Description,
    int? Points,
    DateOnly? DueDate,
    Guid? AssigneeId,
    TaskType Type = TaskType.Feature,
    string? AcceptanceCriteria = null);

public record BulkCreateTasksCommand(
    Guid ProjectId,
    IReadOnlyList<BulkTaskItem> Tasks,
    Guid ActorId,
    string? IpAddress,
    string ActorRole = "") : IRequest<ServiceResult<BulkCreateTasksResult>>;

public record BulkCreateTasksResult(int Created, IReadOnlyList<BulkCreateFailure> Failed);

public record BulkCreateFailure(int Index, string Error);

public class BulkCreateTasksHandler : IRequestHandler<BulkCreateTasksCommand, ServiceResult<BulkCreateTasksResult>>
{
    private readonly ITaskRepository _tasks;
    private readonly IProjectRepository _projects;
    private readonly IEngineerRepository _engineers;
    private readonly IAuditLogRepository _audit;
    private readonly IProjectAccessPolicy _access;

    public BulkCreateTasksHandler(
        ITaskRepository tasks,
        IProjectRepository projects,
        IEngineerRepository engineers,
        IAuditLogRepository audit,
        IProjectAccessPolicy access)
    {
        _tasks = tasks;
        _projects = projects;
        _engineers = engineers;
        _audit = audit;
        _access = access;
    }

    public async Task<ServiceResult<BulkCreateTasksResult>> Handle(BulkCreateTasksCommand cmd, CancellationToken ct)
    {
        if (cmd.Tasks.Count == 0)
            return ServiceResult<BulkCreateTasksResult>.Fail("BUSINESS_RULE_VIOLATION", "Tasks list cannot be empty.");

        if (cmd.Tasks.Count > 200)
            return ServiceResult<BulkCreateTasksResult>.Fail("BUSINESS_RULE_VIOLATION", "Cannot import more than 200 tasks at once.");

        var project = await _projects.GetByIdAsync(cmd.ProjectId, ct);
        if (project is null || project.Status == Domain.Projects.ProjectStatus.Archived)
            return ServiceResult<BulkCreateTasksResult>.Fail("NOT_FOUND", $"Project '{cmd.ProjectId}' not found or archived.");
        if (project.Status == Domain.Projects.ProjectStatus.Paused)
            return ServiceResult<BulkCreateTasksResult>.Fail("BUSINESS_RULE_VIOLATION", "This project is paused — cannot add tasks to it.");

        // Department heads may only bulk-import into projects they can access; PM/PMO are global.
        if (!await _access.CanAccessProjectAsync(cmd.ProjectId, cmd.ActorId, cmd.ActorRole, ct))
            return ServiceResult<BulkCreateTasksResult>.Fail("FORBIDDEN", "You do not have access to this project.");

        var failures = new List<BulkCreateFailure>();
        var created = 0;
        var assigneeIds = new HashSet<Guid>();
        var taskNumbers = new TaskNumberAllocator(_tasks);

        for (var i = 0; i < cmd.Tasks.Count; i++)
        {
            var item = cmd.Tasks[i];

            if (string.IsNullOrWhiteSpace(item.Title))
            {
                failures.Add(new BulkCreateFailure(i, "Title is required."));
                continue;
            }

            // Points is optional — same "0 means ungroomed" convention as single-task create;
            // an explicitly out-of-range value is still rejected rather than silently clamped.
            if (item.Points is < 1 or > 13)
            {
                failures.Add(new BulkCreateFailure(i, "Points must be between 1 and 13."));
                continue;
            }

            if (item.AssigneeId.HasValue)
            {
                var engineer = await _engineers.GetByIdAsync(item.AssigneeId.Value, ct);
                if (engineer is null || !engineer.IsActive)
                {
                    failures.Add(new BulkCreateFailure(i, $"Assignee '{item.AssigneeId}' not found or inactive."));
                    continue;
                }
            }

            var points = item.Points ?? 0;

            // Consistent with single-task create: a due date becomes mandatory once the row
            // carries an assignee or a real point estimate.
            if (item.DueDate is null && (item.AssigneeId.HasValue || points != 0))
            {
                failures.Add(new BulkCreateFailure(i, "A due date is required once an assignee or points is set."));
                continue;
            }

            var task = PulseTask.Create(item.Title, points, cmd.ProjectId, item.Type, item.DueDate, cmd.ActorId);
            task.AssignTaskNumber(await taskNumbers.NextAsync(cmd.ProjectId, ct));

            if (item.Description is not null || item.AcceptanceCriteria is not null)
                task.UpdateDetails(item.Title,
                    DescriptionSanitizer.Sanitize(item.Description),
                    DescriptionSanitizer.Sanitize(item.AcceptanceCriteria),
                    points, item.DueDate, cmd.ActorId);

            if (item.AssigneeId.HasValue)
            {
                task.Assign(item.AssigneeId.Value, cmd.ActorId);
                assigneeIds.Add(item.AssigneeId.Value);
            }

            await _tasks.AddAsync(task, ct);
            created++;
        }

        if (created > 0)
        {
            await _tasks.SaveChangesAsync(ct);

            // Assignment grants project access: ensure each assignee is a project member
            // (idempotent, additive — membership is never removed on reassignment).
            foreach (var assigneeId in assigneeIds)
                await _projects.AddMemberAsync(cmd.ProjectId, assigneeId, ct);

            await _audit.LogAsync("TASK_CREATED", cmd.ActorId, cmd.IpAddress,
                $"Bulk-imported {created} task(s) into project {cmd.ProjectId}", ct);
        }

        return ServiceResult<BulkCreateTasksResult>.Ok(new BulkCreateTasksResult(created, failures));
    }
}
