using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Common;
using MediatR;

namespace Pulse.Application.Sprints.Commands;

public record UpdateSprintCommand(
    Guid SprintId,
    string? Name,
    string? Goal,
    DateOnly? StartDate,
    DateOnly? EndDate,
    bool? Activate,
    bool? Complete,
    int? CapacityPoints,
    DateOnly? ShowAndTellDate,
    string? ShowAndTellNotes,
    Guid ActorId,
    string? IpAddress,
    string ActorRole = "",
    string? DueDateChangeReason = null) : IRequest<ServiceResult<SprintDto>>;

public class UpdateSprintHandler : IRequestHandler<UpdateSprintCommand, ServiceResult<SprintDto>>
{
    private readonly ISprintRepository _sprints;
    private readonly IProjectRepository _projects;
    private readonly ITaskRepository _tasks;
    private readonly IAuditLogRepository _audit;
    private readonly IProjectAccessPolicy _access;

    public UpdateSprintHandler(ISprintRepository sprints, IProjectRepository projects, ITaskRepository tasks, IAuditLogRepository audit, IProjectAccessPolicy access)
    {
        _sprints = sprints;
        _projects = projects;
        _tasks = tasks;
        _audit = audit;
        _access = access;
    }

    public async Task<ServiceResult<SprintDto>> Handle(UpdateSprintCommand cmd, CancellationToken ct)
    {
        var sprint = await _sprints.GetByIdAsync(cmd.SprintId, ct);
        if (sprint is null)
            return ServiceResult<SprintDto>.Fail("NOT_FOUND", $"Sprint '{cmd.SprintId}' not found.");

        if (!await _access.CanAccessTeamAsync(sprint.TeamId, cmd.ActorId, cmd.ActorRole, ct))
            return ServiceResult<SprintDto>.Fail("FORBIDDEN", "You do not have access to this sprint.");

        var oldEndDate = sprint.EndDate;

        // Moving the end date shifts the due date of every not-yet-done task that has one, so it needs
        // one stated reason (recorded on each shifted task). Checked up front so nothing is saved
        // before the reason is known. A sprint whose tasks have no due dates to move isn't asked.
        var dueDateReason = cmd.DueDateChangeReason?.Trim();
        if (cmd.EndDate.HasValue && cmd.EndDate.Value != oldEndDate)
        {
            var tasksToShift = await _tasks.GetBySprintAsync(sprint.Id, ct);
            if (tasksToShift.Any(t => t.DueDate.HasValue && t.Status != Domain.Tasks.TaskStatus.Done))
            {
                if (string.IsNullOrEmpty(dueDateReason))
                    return ServiceResult<SprintDto>.Fail("VALIDATION_ERROR",
                        "A reason is required when changing the sprint end date, because it moves task due dates.");
                if (dueDateReason.Length > 500)
                    return ServiceResult<SprintDto>.Fail("VALIDATION_ERROR", "The due date change reason must be 500 characters or fewer.");
            }
        }

        try
        {
            if (cmd.Name is not null || cmd.Goal is not null || cmd.StartDate.HasValue || cmd.EndDate.HasValue)
            {
                sprint.UpdateDetails(
                    cmd.Name ?? sprint.Name,
                    cmd.Goal is not null ? DescriptionSanitizer.Sanitize(cmd.Goal) : sprint.Goal,
                    cmd.StartDate ?? sprint.StartDate,
                    cmd.EndDate ?? sprint.EndDate);
            }

            if (cmd.Activate == true)
                sprint.Activate();

            if (cmd.Complete == true)
                sprint.Complete();

            if (cmd.CapacityPoints.HasValue)
                sprint.SetCapacity(cmd.CapacityPoints.Value > 0 ? cmd.CapacityPoints : null);

            if (cmd.ShowAndTellDate.HasValue || cmd.ShowAndTellNotes is not null)
                sprint.SetCeremony(cmd.ShowAndTellDate ?? sprint.ShowAndTellDate, cmd.ShowAndTellNotes ?? sprint.ShowAndTellNotes);
        }
        catch (DomainException ex)
        {
            return ServiceResult<SprintDto>.Fail("BUSINESS_RULE_VIOLATION", ex.Message);
        }

        await _sprints.SaveChangesAsync(ct);

        // The sprint's end date moved — shift due dates of tasks already in it by the same delta,
        // so they keep their position relative to the sprint instead of ending up outside its new range.
        var endDateDelta = sprint.EndDate.DayNumber - oldEndDate.DayNumber;
        if (endDateDelta != 0)
        {
            var sprintTasks = await _tasks.GetBySprintAsync(sprint.Id, ct);
            foreach (var task in sprintTasks)
                task.ShiftDueDate(endDateDelta, cmd.ActorId, dueDateReason);
            await _tasks.SaveChangesAsync(ct);
        }

        await _audit.LogAsync("SPRINT_UPDATED", cmd.ActorId, cmd.IpAddress,
            $"Updated sprint {sprint.Id} '{sprint.Name}' — status: {sprint.Status}", ct);

        string? projectName = null;
        if (sprint.ProjectId.HasValue)
        {
            var names = await _projects.GetNamesByIdsAsync([sprint.ProjectId.Value], ct);
            names.TryGetValue(sprint.ProjectId.Value, out projectName);
        }

        return ServiceResult<SprintDto>.Ok(SprintDto.From(sprint, projectName));
    }
}
