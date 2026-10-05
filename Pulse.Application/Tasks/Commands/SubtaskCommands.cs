using Pulse.Application.Auth;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Tasks;
using MediatR;

namespace Pulse.Application.Tasks.Commands;

public record AddSubtaskCommand(Guid TaskId, string Title, Guid ActorId, string ActorRole) : IRequest<ServiceResult<SubtaskDto>>;

public class AddSubtaskHandler : IRequestHandler<AddSubtaskCommand, ServiceResult<SubtaskDto>>
{
    private readonly ITaskRepository _tasks;
    private readonly ISubtaskRepository _subtasks;

    public AddSubtaskHandler(ITaskRepository tasks, ISubtaskRepository subtasks)
    {
        _tasks = tasks;
        _subtasks = subtasks;
    }

    public async Task<ServiceResult<SubtaskDto>> Handle(AddSubtaskCommand cmd, CancellationToken ct)
    {
        var task = await _tasks.GetByIdAsync(cmd.TaskId, ct);
        if (task is null)
            return ServiceResult<SubtaskDto>.Fail("NOT_FOUND", $"Task '{cmd.TaskId}' not found.");

        var allowed = task.AssigneeId == cmd.ActorId
            || CapabilityRegistry.ResolveFor(cmd.ActorRole).Contains(CapabilityRegistry.TeamLeadOrAbove);
        if (!allowed)
            return ServiceResult<SubtaskDto>.Fail("FORBIDDEN", "Only the assignee, a team lead, or above may add subtasks.");

        if (string.IsNullOrWhiteSpace(cmd.Title))
            return ServiceResult<SubtaskDto>.Fail("VALIDATION_ERROR", "Subtask title is required.");

        var subtask = Subtask.Create(cmd.TaskId, cmd.Title, cmd.ActorId);
        await _subtasks.AddAsync(subtask, ct);
        await _subtasks.SaveChangesAsync(ct);

        return ServiceResult<SubtaskDto>.Ok(SubtaskDto.From(subtask));
    }
}

public record ToggleSubtaskCommand(Guid SubtaskId, bool IsDone, Guid ActorId, string ActorRole, string? IpAddress = null) : IRequest<ServiceResult<SubtaskDto>>;

public class ToggleSubtaskHandler : IRequestHandler<ToggleSubtaskCommand, ServiceResult<SubtaskDto>>
{
    private readonly ITaskRepository _tasks;
    private readonly ISubtaskRepository _subtasks;
    private readonly IAuditLogRepository _audit;

    public ToggleSubtaskHandler(ITaskRepository tasks, ISubtaskRepository subtasks, IAuditLogRepository audit)
    {
        _tasks = tasks;
        _subtasks = subtasks;
        _audit = audit;
    }

    public async Task<ServiceResult<SubtaskDto>> Handle(ToggleSubtaskCommand cmd, CancellationToken ct)
    {
        var subtask = await _subtasks.GetByIdAsync(cmd.SubtaskId, ct);
        if (subtask is null)
            return ServiceResult<SubtaskDto>.Fail("NOT_FOUND", $"Subtask '{cmd.SubtaskId}' not found.");

        var task = await _tasks.GetByIdAsync(subtask.TaskId, ct);
        if (task is null)
            return ServiceResult<SubtaskDto>.Fail("NOT_FOUND", $"Task '{subtask.TaskId}' not found.");

        var allowed = task.AssigneeId == cmd.ActorId
            || subtask.AssigneeId == cmd.ActorId
            || CapabilityRegistry.ResolveFor(cmd.ActorRole).Contains(CapabilityRegistry.TeamLeadOrAbove);
        if (!allowed)
            return ServiceResult<SubtaskDto>.Fail("FORBIDDEN", "Only the assignee, a team lead, or above may check off subtasks.");

        // Only a genuine not-done -> done transition is reportable — un-checking (or re-toggling
        // an already-done subtask to done again) is corrective noise, not a new completion event.
        var justCompleted = !subtask.IsDone && cmd.IsDone;
        subtask.SetDone(cmd.IsDone, cmd.ActorId);
        if (justCompleted)
            task.RecordSubtaskCompleted(cmd.ActorId, subtask.Title);

        await _subtasks.SaveChangesAsync(ct);

        if (justCompleted)
            await _audit.LogAsync("SUBTASK_COMPLETED", cmd.ActorId, cmd.IpAddress,
                $"Subtask '{subtask.Id}' on task '{task.Id}' marked done", ct);

        return ServiceResult<SubtaskDto>.Ok(SubtaskDto.From(subtask));
    }
}

public record DeleteSubtaskCommand(Guid SubtaskId, Guid ActorId, string ActorRole) : IRequest<ServiceResult<bool>>;

public class DeleteSubtaskHandler : IRequestHandler<DeleteSubtaskCommand, ServiceResult<bool>>
{
    private readonly ITaskRepository _tasks;
    private readonly ISubtaskRepository _subtasks;

    public DeleteSubtaskHandler(ITaskRepository tasks, ISubtaskRepository subtasks)
    {
        _tasks = tasks;
        _subtasks = subtasks;
    }

    public async Task<ServiceResult<bool>> Handle(DeleteSubtaskCommand cmd, CancellationToken ct)
    {
        var subtask = await _subtasks.GetByIdAsync(cmd.SubtaskId, ct);
        if (subtask is null)
            return ServiceResult<bool>.Fail("NOT_FOUND", $"Subtask '{cmd.SubtaskId}' not found.");

        var task = await _tasks.GetByIdAsync(subtask.TaskId, ct);
        if (task is null)
            return ServiceResult<bool>.Fail("NOT_FOUND", $"Task '{subtask.TaskId}' not found.");

        var allowed = task.AssigneeId == cmd.ActorId
            || CapabilityRegistry.ResolveFor(cmd.ActorRole).Contains(CapabilityRegistry.TeamLeadOrAbove);
        if (!allowed)
            return ServiceResult<bool>.Fail("FORBIDDEN", "Only the assignee, a team lead, or above may delete subtasks.");

        await _subtasks.DeleteAsync(subtask, ct);
        await _subtasks.SaveChangesAsync(ct);

        return ServiceResult<bool>.Ok(true);
    }
}
