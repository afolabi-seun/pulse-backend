using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Overwork;
using Pulse.Application.Tasks;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Tasks.Commands;

public record BulkReassignCommand(
    IReadOnlyList<Guid> TaskIds,
    Guid TargetEngineerId,
    Guid ActorId,
    string? IpAddress,
    string ActorRole = "") : IRequest<ServiceResult<BulkReassignResult>>;

public record BulkReassignResult(
    int TasksReassigned,
    OverworkSignalsDto SignalsAfter,
    bool TriggeredOverworkWarning);

public class BulkReassignHandler : IRequestHandler<BulkReassignCommand, ServiceResult<BulkReassignResult>>
{
    private readonly ITaskRepository _tasks;
    private readonly IEngineerRepository _engineers;
    private readonly IOverworkOverrideRepository _overrides;
    private readonly IAuditLogRepository _auditLog;
    private readonly IEscalationEventRepository _escalationEvents;
    private readonly OverworkSignalsCalculator _calculator;
    private readonly IProjectAccessPolicy _access;
    private readonly IProjectRepository _projects;
    private readonly ITeamRepository _teams;

    public BulkReassignHandler(
        ITaskRepository tasks,
        IEngineerRepository engineers,
        IOverworkOverrideRepository overrides,
        IAuditLogRepository auditLog,
        IEscalationEventRepository escalationEvents,
        OverworkSignalsCalculator calculator,
        IProjectAccessPolicy access,
        IProjectRepository projects,
        ITeamRepository teams)
    {
        _tasks = tasks;
        _engineers = engineers;
        _overrides = overrides;
        _auditLog = auditLog;
        _escalationEvents = escalationEvents;
        _calculator = calculator;
        _access = access;
        _projects = projects;
        _teams = teams;
    }

    public async Task<ServiceResult<BulkReassignResult>> Handle(BulkReassignCommand command, CancellationToken ct)
    {
        if (command.TaskIds.Count == 0)
            return ServiceResult<BulkReassignResult>.Fail("VALIDATION_ERROR", "At least one task ID is required.");

        var engineer = await _engineers.GetByIdAsync(command.TargetEngineerId, ct);
        if (engineer is null)
            return ServiceResult<BulkReassignResult>.Fail("NOT_FOUND", $"Engineer '{command.TargetEngineerId}' not found.");
        if (!engineer.IsActive)
            return ServiceResult<BulkReassignResult>.Fail("VALIDATION_ERROR", "Cannot assign tasks to an inactive engineer.");

        var tasksToReassign = new List<Domain.Tasks.PulseTask>();
        foreach (var taskId in command.TaskIds.Distinct())
        {
            var task = await _tasks.GetByIdAsync(taskId, ct);
            if (task is null)
                return ServiceResult<BulkReassignResult>.Fail("NOT_FOUND", $"Task '{taskId}' not found.");
            if (task.Status == Domain.Tasks.TaskStatus.Done)
                return ServiceResult<BulkReassignResult>.Fail("VALIDATION_ERROR", $"Task '{taskId}' is already done and cannot be reassigned.");

            var qaCheck = await QaAssignmentPolicy.ValidateAsync(task, engineer, command.ActorRole, _tasks, _teams, ct);
            if (!qaCheck.IsAllowed)
                return ServiceResult<BulkReassignResult>.Fail(qaCheck.ErrorCode!, qaCheck.ErrorMessage!);

            if (!await _access.CanAccessProjectAsync(task.ProjectId, command.ActorId, command.ActorRole, ct))
                return ServiceResult<BulkReassignResult>.Fail("FORBIDDEN", $"You do not have access to task '{taskId}'.");
            tasksToReassign.Add(task);
        }

        var (_, activeTasks) = await _engineers.GetWithActiveTasksAsync(command.TargetEngineerId, ct);
        var @override = await _overrides.GetActiveAsync(command.TargetEngineerId, ct);

        // Reassign all tasks
        foreach (var task in tasksToReassign)
        {
            task.Assign(command.TargetEngineerId, command.ActorId);
            await _auditLog.LogAsync("TASK_REASSIGNED", command.ActorId, command.IpAddress,
                $"Task '{task.Id}' bulk-reassigned to engineer '{command.TargetEngineerId}'", ct);
        }

        await _tasks.SaveChangesAsync(ct);

        // Assignment grants project access: ensure the target engineer is a member of
        // every project they were reassigned into (idempotent, additive).
        foreach (var projectId in tasksToReassign.Select(t => t.ProjectId).Distinct())
            await _projects.AddMemberAsync(projectId, command.TargetEngineerId, ct);

        // Re-arm escalation events so the scanner fires on the new engineer's schedule.
        foreach (var task in tasksToReassign)
            await _escalationEvents.ClearForTaskAsync(task.Id, ct);

        // Compute signals after reassignment (include newly reassigned tasks)
        var allTasksAfter = activeTasks
            .Where(t => !tasksToReassign.Any(r => r.Id == t.Id))
            .Concat(tasksToReassign)
            .ToList();

        var (signalsAfter, wouldFlag) = _calculator.Compute(engineer, allTasksAfter, @override);

        return ServiceResult<BulkReassignResult>.Ok(new BulkReassignResult(
            tasksToReassign.Count,
            OverworkSignalsDto.From(signalsAfter, wouldFlag, @override?.IsActive == true),
            wouldFlag));
    }
}
