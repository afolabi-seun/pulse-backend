namespace Pulse.Application.Reports;

public record WorkstreamCheckInDto(Guid ProjectId, int ExpectedEngineers, int CheckedInEngineers, int CoveragePct);

public record WeeklyReportDto(
    Guid TeamId,
    string TeamName,
    string WeekOf,
    int TotalDeliveredPoints,
    int PreviousWeekPoints,
    TeamUtilizationDto Utilization,
    IReadOnlyList<ProjectHealthDto> Workstreams,
    IReadOnlyList<BlockerAgingDto> Blockers,
    TeamComplianceDto Compliance,
    IReadOnlyList<WorkstreamCheckInDto> CheckInCoverage,
    string ExecutiveSummary,
    string KeyAccomplishments,
    string PlannedNextWeek,
    string ResourcingNotes,
    string SuggestedExecutiveSummary,
    string SuggestedKeyAccomplishments,
    string SuggestedPlannedNextWeek,
    string SuggestedResourcingNotes,
    Guid? SubmittedById,
    string? SubmittedByName,
    DateTime? SubmittedAt,
    DateTime? UpdatedAt,
    bool IsNew);
