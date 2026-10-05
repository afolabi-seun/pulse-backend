namespace Pulse.Application.Overwork;

/// <summary>Merges a department's override onto the global thresholds, per field — never mutates
/// either input, always returns a fresh instance so the shared global singleton is untouched.</summary>
public static class DepartmentThresholdResolver
{
    public static OverworkThresholds Resolve(OverworkThresholds global, DepartmentThresholdOverride? departmentOverride)
    {
        if (departmentOverride is null)
            return global;

        return new OverworkThresholds
        {
            LoadVsBaselineRatio    = departmentOverride.LoadVsBaselineRatio    ?? global.LoadVsBaselineRatio,
            MaxConcurrentTasks     = departmentOverride.MaxConcurrentTasks     ?? global.MaxConcurrentTasks,
            StaleCycleMultiplier   = departmentOverride.StaleCycleMultiplier   ?? global.StaleCycleMultiplier,
            SignalsRequiredToFlag  = departmentOverride.SignalsRequiredToFlag  ?? global.SignalsRequiredToFlag,
            // Not department-overridable — carried through from the global singleton as-is.
            EscalationT3Days       = global.EscalationT3Days,
            EscalationT3ElapsedPct = global.EscalationT3ElapsedPct,
            EscalationT1Days       = global.EscalationT1Days,
            EscalationT1ElapsedPct = global.EscalationT1ElapsedPct,
            EscalationT3MinHours   = global.EscalationT3MinHours,
            EscalationT1MinHours   = global.EscalationT1MinHours,
            QaLeadTimeDays         = global.QaLeadTimeDays,
            PointScale             = global.PointScale,
            PriorityScale          = global.PriorityScale,
        };
    }
}
