using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Overwork;
using MediatR;

namespace Pulse.Application.Thresholds;

public record UpsertDepartmentThresholdCommand(
    string Department,
    double? LoadVsBaselineRatio,
    int? MaxConcurrentTasks,
    double? StaleCycleMultiplier,
    int? SignalsRequiredToFlag,
    Guid ActorId,
    string? IpAddress) : IRequest<ServiceResult<DepartmentThresholdDto>>;

/// <summary>Creates or replaces a department's override in one call — a field left null means
/// that field inherits the global default, not "leave whatever was there before" (unlike
/// UpdateThresholdsCommand's per-field patch semantics on the single global object).</summary>
public class UpsertDepartmentThresholdHandler : IRequestHandler<UpsertDepartmentThresholdCommand, ServiceResult<DepartmentThresholdDto>>
{
    private readonly IDepartmentThresholdRepository _repo;
    private readonly IAuditLogRepository _audit;

    public UpsertDepartmentThresholdHandler(IDepartmentThresholdRepository repo, IAuditLogRepository audit)
    {
        _repo = repo;
        _audit = audit;
    }

    public async Task<ServiceResult<DepartmentThresholdDto>> Handle(UpsertDepartmentThresholdCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.Department))
            return ServiceResult<DepartmentThresholdDto>.Fail("VALIDATION_ERROR", "Department is required.");

        var entity = new DepartmentThresholdOverride
        {
            Department = cmd.Department.Trim(),
            LoadVsBaselineRatio = cmd.LoadVsBaselineRatio,
            MaxConcurrentTasks = cmd.MaxConcurrentTasks,
            StaleCycleMultiplier = cmd.StaleCycleMultiplier,
            SignalsRequiredToFlag = cmd.SignalsRequiredToFlag,
            UpdatedBy = cmd.ActorId,
            UpdatedAt = DateTime.UtcNow,
        };
        await _repo.UpsertAsync(entity, ct);

        await _audit.LogAsync("DEPARTMENT_THRESHOLD_UPDATED", cmd.ActorId, cmd.IpAddress,
            $"Overwork thresholds overridden for department '{entity.Department}'", ct);

        return ServiceResult<DepartmentThresholdDto>.Ok(new DepartmentThresholdDto(
            entity.Department, entity.LoadVsBaselineRatio, entity.MaxConcurrentTasks,
            entity.StaleCycleMultiplier, entity.SignalsRequiredToFlag, null, entity.UpdatedAt));
    }
}
