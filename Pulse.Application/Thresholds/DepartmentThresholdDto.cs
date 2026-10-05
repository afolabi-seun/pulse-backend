namespace Pulse.Application.Thresholds;

public record DepartmentThresholdDto(
    string Department,
    double? LoadVsBaselineRatio,
    int? MaxConcurrentTasks,
    double? StaleCycleMultiplier,
    int? SignalsRequiredToFlag,
    string? UpdatedByName,
    DateTime? UpdatedAt);
