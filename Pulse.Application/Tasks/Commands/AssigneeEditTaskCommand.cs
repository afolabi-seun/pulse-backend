using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Common;
using MediatR;
using DomainTaskStatus = Pulse.Domain.Tasks.TaskStatus;

namespace Pulse.Application.Tasks.Commands;

/// <summary>
/// Self-service editing for an engineer's own assigned task — the narrow gap left by
/// PATCH /tasks/{id} being TeamLeadOrAbove-gated: an assignee has no way to correct their own
/// task's description or push its due date out without asking someone else to do it for them.
/// Deliberately narrow: only Description and DueDate, only for the task's current assignee.
/// Points stay out of reach here on purpose — they still go through Planning Poker +
/// department-head approval (see SubmitEstimateForApprovalCommand/ApproveEstimateCommand), which
/// exists specifically to keep an assignee from unilaterally inflating their own scope.
/// </summary>
public record AssigneeEditTaskCommand(
    Guid TaskId,
    string? Description,
    DateOnly? DueDate,
    Guid ActorId,
    string? IpAddress,
    string? DueDateChangeReason = null) : IRequest<ServiceResult<TaskDto>>;

public class AssigneeEditTaskHandler : IRequestHandler<AssigneeEditTaskCommand, ServiceResult<TaskDto>>
{
    private readonly ITaskRepository _tasks;
    private readonly ISprintRepository _sprints;
    private readonly IAuditLogRepository _audit;

    public AssigneeEditTaskHandler(ITaskRepository tasks, ISprintRepository sprints, IAuditLogRepository audit)
    {
        _tasks = tasks;
        _sprints = sprints;
        _audit = audit;
    }

    public async Task<ServiceResult<TaskDto>> Handle(AssigneeEditTaskCommand cmd, CancellationToken ct)
    {
        var task = await _tasks.GetByIdAsync(cmd.TaskId, ct);
        if (task is null)
            return ServiceResult<TaskDto>.Fail("NOT_FOUND", $"Task '{cmd.TaskId}' not found.");

        if (task.AssigneeId != cmd.ActorId)
            return ServiceResult<TaskDto>.Fail("FORBIDDEN", "You can only edit a task assigned to you.");

        // A due date can't outrun the sprint it was committed against — a due date is also the
        // trigger every escalation/report on this task relies on, so letting the assignee push it
        // past the sprint's own end date would quietly undermine the sprint's commitment. A task
        // with no sprint has no such anchor, so no upper bound applies there.
        if (cmd.DueDate.HasValue && task.Status != DomainTaskStatus.Done && task.SprintId is Guid sprintId)
        {
            var sprint = await _sprints.GetByIdAsync(sprintId, ct);
            if (sprint is not null && cmd.DueDate.Value > sprint.EndDate)
                return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION",
                    $"Due date can't be later than this task's sprint end date ({sprint.EndDate:yyyy-MM-dd}).");
        }

        // Moving an EXISTING due date needs a stated reason; setting the first date does not. A
        // done/in-QA task is rejected by UpdateDetails below, so its domain error is left to surface.
        var reason = cmd.DueDateChangeReason?.Trim();
        if (cmd.DueDate.HasValue && task.DueDate.HasValue && cmd.DueDate != task.DueDate
            && task.Status is not (DomainTaskStatus.Done or DomainTaskStatus.InQa))
        {
            if (string.IsNullOrEmpty(reason))
                return ServiceResult<TaskDto>.Fail("VALIDATION_ERROR", "A reason is required when changing the due date.");
            if (reason.Length > 500)
                return ServiceResult<TaskDto>.Fail("VALIDATION_ERROR", "The due date change reason must be 500 characters or fewer.");
        }

        try
        {
            task.UpdateDetails(
                task.Title,
                cmd.Description is not null ? DescriptionSanitizer.Sanitize(cmd.Description) : task.Description,
                task.AcceptanceCriteria,
                task.Points,
                cmd.DueDate ?? task.DueDate,
                cmd.ActorId,
                reason);
        }
        catch (DomainException ex)
        {
            return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION", ex.Message);
        }

        await _tasks.SaveChangesAsync(ct);
        await _audit.LogAsync("TASK_ASSIGNEE_EDITED", cmd.ActorId, cmd.IpAddress,
            $"Task '{task.Id}' description/due date edited by its own assignee", ct);

        return ServiceResult<TaskDto>.Ok(TaskDto.From(task));
    }
}
