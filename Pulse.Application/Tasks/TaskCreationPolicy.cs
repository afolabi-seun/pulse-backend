using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Projects;
using Pulse.Domain.Tasks;

namespace Pulse.Application.Tasks;

/// <summary>Everything a caller must already have resolved before building a task: real IDs, not
/// names/emails — CreateTaskCommand already has these directly; ImportTasksHandler resolves them
/// itself (project by name, assignee by email, epic by name) before calling in. Description and
/// AcceptanceCriteria must already be sanitized by the caller — CreateTaskCommand's rich-text
/// editor and the CSV importer's plain-text rows use different sanitizers, so that stays the
/// caller's job, not this shared core's.</summary>
public record TaskCreationInput(
    string Title,
    string? Description,
    string? AcceptanceCriteria,
    int Points,
    DateOnly? DueDate,
    Guid ProjectId,
    Guid? AssigneeId,
    TaskType Type,
    Guid? EpicId,
    bool RequiresQa,
    Discipline? Discipline,
    Guid? ParentTaskId,
    int? Priority,
    Guid ActorId,
    string ActorRole,
    int TaskNumber,
    string? ExternalReference = null,
    bool RequiresFrontendHandoff = false);

/// <summary>
/// The validation and construction shared by every path that creates a brand-new task
/// (CreateTaskCommand, ImportTasksHandler): the project must exist and not be archived/paused,
/// the actor must be able to access it, a due date is required once the task has enough shape to
/// schedule (an assignee, real points, or a priority), and the domain object itself is built the
/// same way. Each caller keeps its own side effects — audit logging, notifications/email, and
/// save/batching strategy — since those differ deliberately (e.g. Import stays silent and batches
/// a single save per file; a manual create notifies the new assignee immediately).
/// </summary>
public static class TaskCreationPolicy
{
    public static async Task<ServiceResult<PulseTask>> ValidateAndBuildAsync(
        TaskCreationInput input, IProjectRepository projects, IProjectAccessPolicy access, CancellationToken ct)
    {
        var project = await projects.GetByIdAsync(input.ProjectId, ct);
        if (project is null || project.Status == ProjectStatus.Archived)
            return ServiceResult<PulseTask>.Fail("NOT_FOUND", $"Project '{input.ProjectId}' not found or archived.");
        if (project.Status == ProjectStatus.Paused)
            return ServiceResult<PulseTask>.Fail("BUSINESS_RULE_VIOLATION", "This project is paused — cannot add tasks to it.");

        // Department heads may only create tasks in projects they can access; PM/PMO are global.
        if (!await access.CanAccessProjectAsync(input.ProjectId, input.ActorId, input.ActorRole, ct))
            return ServiceResult<PulseTask>.Fail("FORBIDDEN", "You do not have access to this project.");

        // A due date is only mandatory once the task has enough shape to be scheduled — an
        // assignee, a real point estimate, or a priority. A bare, ungroomed backlog item can go
        // without one.
        if (input.DueDate is null && (input.AssigneeId.HasValue || input.Points != 0 || input.Priority.HasValue))
            return ServiceResult<PulseTask>.Fail("BUSINESS_RULE_VIOLATION",
                "A due date is required once an assignee, points, or priority is set.");

        var task = PulseTask.Create(input.Title, input.Points, input.ProjectId, input.Type, input.DueDate, input.ActorId);
        task.AssignTaskNumber(input.TaskNumber);
        if (input.ExternalReference is not null)
            task.SetExternalReference(input.ExternalReference);

        if (input.Description is not null || input.AcceptanceCriteria is not null)
            task.UpdateDetails(input.Title, input.Description, input.AcceptanceCriteria, input.Points, input.DueDate, input.ActorId);

        if (input.AssigneeId.HasValue)
            task.Assign(input.AssigneeId.Value, input.ActorId);

        if (input.EpicId.HasValue)
            task.AssignToEpic(input.EpicId);

        if (input.RequiresQa)
            task.SetRequiresQa(true);

        if (input.RequiresFrontendHandoff)
            task.SetRequiresFrontendHandoff(true);

        if (input.Discipline.HasValue)
            task.SetDiscipline(input.Discipline.Value);

        if (input.ParentTaskId.HasValue)
            task.SetParentTaskId(input.ParentTaskId.Value);

        if (input.Priority.HasValue)
            task.SetPriority(input.Priority.Value);

        return ServiceResult<PulseTask>.Ok(task);
    }
}
