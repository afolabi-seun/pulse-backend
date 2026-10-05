namespace Pulse.Application.Reports;

public record EscalationReportEntry(
    Guid TaskId,
    string Title,
    Guid? AssigneeId,
    DateOnly? DueDate,
    string Level,
    string? AssigneeName = null,
    string? ProjectName = null,
    string? TaskKey = null);

public record BlockerReportEntry(
    Guid TaskId,
    string Title,
    Guid? AssigneeId,
    string Reason,
    string? AssigneeName = null,
    string? ProjectName = null,
    string? TaskKey = null,
    int DaysBlocked = 0);

public record LeadershipReportDto(
    string WeekOf,
    int TotalDeliveredPoints,
    int PreviousWeekPoints,
    IReadOnlyList<EngineerUtilizationEntry> Engineers,
    IReadOnlyList<EscalationReportEntry> Escalations,
    IReadOnlyList<BlockerReportEntry> Blockers,
    // True when the caller (a department head) is scoped to their own department for
    // Engineers/Escalations/Blockers above, while TotalDeliveredPoints/PreviousWeekPoints stay
    // org-wide aggregate velocity regardless — those two numbers describe a different population
    // than everything else on the page whenever this is true, which the frontend uses to label
    // them "(org-wide)" instead of leaving the mismatch implied by a single "Points delivered"
    // label. Always false for an org-wide caller (PMO/Executive/HR/etc.) — there, delivered
    // points and everything else already agree, so no qualifier is needed.
    bool IsCallerDepartmentScoped);
