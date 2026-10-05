using Pulse.Domain.Organizations;

namespace Pulse.Application.Overwork;

/// <summary>A department's override of the four overwork-detection thresholds, falling back
/// per-field to the global <see cref="OverworkThresholds"/> when a field is left null. Escalation
/// timing, QA lead time, and the point scale are org-wide only — not part of what a department can
/// override.</summary>
public class DepartmentThresholdOverride
{
    /// <summary>Overrides are per organization: (OrganizationId, Department) is the key.</summary>
    public Guid OrganizationId { get; set; } = Organization.DefaultId;
    public string Department { get; set; } = string.Empty;
    public double? LoadVsBaselineRatio { get; set; }
    public int? MaxConcurrentTasks { get; set; }
    public double? StaleCycleMultiplier { get; set; }
    public int? SignalsRequiredToFlag { get; set; }
    public Guid? UpdatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
