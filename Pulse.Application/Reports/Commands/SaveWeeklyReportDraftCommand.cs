using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Overwork;
using Pulse.Domain.Engineers;
using Pulse.Domain.Reports;
using MediatR;

namespace Pulse.Application.Reports.Commands;

public record SaveWeeklyReportDraftCommand(
    Guid ActorId,
    string ActorRole,
    Guid TeamId,
    DateOnly WeekOf,
    string ExecutiveSummary,
    string KeyAccomplishments,
    string PlannedNextWeek,
    string ResourcingNotes) : IRequest<ServiceResult<WeeklyReportDto>>;

public class SaveWeeklyReportDraftHandler : IRequestHandler<SaveWeeklyReportDraftCommand, ServiceResult<WeeklyReportDto>>
{
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;
    private readonly IProjectRepository _projects;
    private readonly ITaskRepository _tasks;
    private readonly ICheckInRepository _checkIns;
    private readonly ITimeEntryRepository _timeEntries;
    private readonly IWeeklyReportRepository _weeklyReports;
    private readonly IProjectAccessPolicy _access;
    private readonly EngineerWorkloadAssessor _assessor;
    private readonly OverworkThresholds _thresholds;

    public SaveWeeklyReportDraftHandler(
        IEngineerRepository engineers,
        ITeamRepository teams,
        IProjectRepository projects,
        ITaskRepository tasks,
        ICheckInRepository checkIns,
        ITimeEntryRepository timeEntries,
        IWeeklyReportRepository weeklyReports,
        IProjectAccessPolicy access,
        EngineerWorkloadAssessor assessor,
        OverworkThresholds thresholds)
    {
        _engineers = engineers;
        _teams = teams;
        _projects = projects;
        _tasks = tasks;
        _checkIns = checkIns;
        _timeEntries = timeEntries;
        _weeklyReports = weeklyReports;
        _access = access;
        _assessor = assessor;
        _thresholds = thresholds;
    }

    public async Task<ServiceResult<WeeklyReportDto>> Handle(SaveWeeklyReportDraftCommand cmd, CancellationToken ct)
    {
        var authResult = await WeeklyReportAssembler.AuthorizeWriteAsync(cmd.ActorId, cmd.ActorRole, cmd.TeamId, _engineers, _access, ct);
        if (!authResult.IsSuccess)
            return ServiceResult<WeeklyReportDto>.Fail(authResult.ErrorCode!, authResult.ErrorMessage!);

        var weekStart = WeekOf.Monday(cmd.WeekOf);
        var report = await _weeklyReports.GetByTeamAndWeekAsync(cmd.TeamId, weekStart, ct);
        if (report is null)
        {
            report = WeeklyReport.Create(cmd.TeamId, weekStart, cmd.ActorId);
            await _weeklyReports.AddAsync(report, ct);
        }
        report.UpdateDraft(cmd.ExecutiveSummary, cmd.KeyAccomplishments, cmd.PlannedNextWeek, cmd.ResourcingNotes, cmd.ActorId);
        await _weeklyReports.SaveChangesAsync(ct);

        return await WeeklyReportAssembler.BuildAsync(
            cmd.TeamId, weekStart,
            _teams, _engineers, _projects, _tasks, _checkIns, _timeEntries, _weeklyReports,
            _assessor, _thresholds, ct, preloadedReport: report);
    }
}
