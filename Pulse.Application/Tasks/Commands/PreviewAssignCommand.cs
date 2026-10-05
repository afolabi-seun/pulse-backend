using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Overwork;
using MediatR;

namespace Pulse.Application.Tasks.Commands;

public record PreviewAssignCommand(Guid TaskId, Guid TargetEngineerId) : IRequest<ServiceResult<PreviewAssignResult>>;

public record PreviewAssignResult(
    OverworkSignalsDto Before,
    OverworkSignalsDto After,
    bool WouldFlagOverwork);

public class PreviewAssignHandler : IRequestHandler<PreviewAssignCommand, ServiceResult<PreviewAssignResult>>
{
    private readonly ITaskRepository _tasks;
    private readonly IEngineerRepository _engineers;
    private readonly IOverworkOverrideRepository _overrides;
    private readonly OverworkSignalsCalculator _calculator;

    public PreviewAssignHandler(
        ITaskRepository tasks,
        IEngineerRepository engineers,
        IOverworkOverrideRepository overrides,
        OverworkSignalsCalculator calculator)
    {
        _tasks = tasks;
        _engineers = engineers;
        _overrides = overrides;
        _calculator = calculator;
    }

    public async Task<ServiceResult<PreviewAssignResult>> Handle(PreviewAssignCommand command, CancellationToken ct)
    {
        var task = await _tasks.GetByIdAsync(command.TaskId, ct);
        if (task is null)
            return ServiceResult<PreviewAssignResult>.Fail("NOT_FOUND", $"Task '{command.TaskId}' not found.");

        var engineer = await _engineers.GetByIdAsync(command.TargetEngineerId, ct);
        if (engineer is null)
            return ServiceResult<PreviewAssignResult>.Fail("NOT_FOUND", $"Engineer '{command.TargetEngineerId}' not found.");

        var (_, activeTasks) = await _engineers.GetWithActiveTasksAsync(command.TargetEngineerId, ct);
        var @override = await _overrides.GetActiveAsync(command.TargetEngineerId, ct);

        var (beforeSignals, beforeOverworked) = _calculator.Compute(engineer, activeTasks, @override);

        // Simulate adding this task to the engineer's workload
        var simulatedTasks = activeTasks.Append(task).ToList();
        var (afterSignals, afterOverworked) = _calculator.Compute(engineer, simulatedTasks, @override);

        return ServiceResult<PreviewAssignResult>.Ok(new PreviewAssignResult(
            OverworkSignalsDto.From(beforeSignals, beforeOverworked, @override?.IsActive == true),
            OverworkSignalsDto.From(afterSignals, afterOverworked, @override?.IsActive == true),
            afterOverworked));
    }
}
