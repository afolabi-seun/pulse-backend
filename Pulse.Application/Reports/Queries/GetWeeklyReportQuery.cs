using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Overwork;
using MediatR;

namespace Pulse.Application.Reports.Queries;

public record GetWeeklyReportQuery(
    Guid CallerId,
    string CallerRole,
    Guid? TeamId = null,
    DateOnly? WeekOf = null) : IRequest<ServiceResult<WeeklyReportDto>>;

public class GetWeeklyReportHandler : IRequestHandler<GetWeeklyReportQuery, ServiceResult<WeeklyReportDto>>
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

    public GetWeeklyReportHandler(
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

    public async Task<ServiceResult<WeeklyReportDto>> Handle(GetWeeklyReportQuery query, CancellationToken ct)
    {
        var teamResult = await WeeklyReportAssembler.ResolveTeamAsync(query.CallerId, query.CallerRole, query.TeamId, _engineers, _access, ct);
        if (!teamResult.IsSuccess)
            return ServiceResult<WeeklyReportDto>.Fail(teamResult.ErrorCode!, teamResult.ErrorMessage!);

        return await WeeklyReportAssembler.BuildAsync(
            teamResult.Data, query.WeekOf,
            _teams, _engineers, _projects, _tasks, _checkIns, _timeEntries, _weeklyReports,
            _assessor, _thresholds, ct);
    }
}
