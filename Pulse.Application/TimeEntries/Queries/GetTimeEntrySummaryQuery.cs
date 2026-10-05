using Pulse.Application.Auth;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.TimeEntries.Queries;

public record DailyHoursDto(DateOnly Date, decimal Hours);

public record EngineerHoursDto(Guid EngineerId, string Name, decimal TotalHours, IReadOnlyList<DailyHoursDto> DailyHours);

/// <param name="Kind">"project" | "general" (time with no project: meetings, admin, leave, other) | "personal"
/// (everyone's personal tasks folded into one line). The null-id rows are told apart by this.</param>
public record ProjectHoursDto(Guid? ProjectId, string ProjectName, decimal TotalHours, string Kind = "project");

public record TimeEntrySummaryDto(
    string WeekOf, string To, IReadOnlyList<EngineerHoursDto> Engineers, IReadOnlyList<ProjectHoursDto> Projects, decimal TeamTotalHours);

public record GetTimeEntrySummaryQuery(
    DateOnly? WeekOf,
    string CallerRole,
    Guid CallerId,
    // An explicit From/To overrides WeekOf's Monday-Sunday window with an arbitrary range — used
    // by the daily-breakdown view, which isn't bound to calendar-week boundaries the way the
    // weekly roll-up is. Mirrors GetPmoReportQuery's own WeekOf/From/To precedent.
    DateOnly? From = null,
    DateOnly? To = null) : IRequest<ServiceResult<TimeEntrySummaryDto>>;

public class GetTimeEntrySummaryHandler : IRequestHandler<GetTimeEntrySummaryQuery, ServiceResult<TimeEntrySummaryDto>>
{
    private readonly ITimeEntryRepository _timeEntries;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;
    private readonly IProjectRepository _projects;

    public GetTimeEntrySummaryHandler(ITimeEntryRepository timeEntries, IEngineerRepository engineers, ITeamRepository teams, IProjectRepository projects)
    {
        _timeEntries = timeEntries;
        _engineers = engineers;
        _teams = teams;
        _projects = projects;
    }

    private const int MaxRangeDays = 62;

    public async Task<ServiceResult<TimeEntrySummaryDto>> Handle(GetTimeEntrySummaryQuery request, CancellationToken ct)
    {
        DateOnly weekStart, weekEnd;
        if (request.From.HasValue && request.To.HasValue)
        {
            if (request.To.Value < request.From.Value)
                return ServiceResult<TimeEntrySummaryDto>.Fail("VALIDATION_ERROR", "'to' must be on or after 'from'.");
            if (request.To.Value.DayNumber - request.From.Value.DayNumber > MaxRangeDays)
                return ServiceResult<TimeEntrySummaryDto>.Fail("VALIDATION_ERROR", $"Range cannot exceed {MaxRangeDays} days.");
            weekStart = request.From.Value;
            weekEnd = request.To.Value;
        }
        else
        {
            var today = request.WeekOf ?? DateOnly.FromDateTime(DateTime.UtcNow);
            weekStart = WeekOf.Monday(today);
            weekEnd = weekStart.AddDays(6);
        }

        var (allActive, scopedIds) = await TimeSummaryScope.ResolveAsync(request.CallerRole, request.CallerId, _engineers, _teams, ct);

        var hoursByEngineer = await _timeEntries.GetHoursByEngineerInRangeAsync(weekStart, weekEnd, scopedIds.ToList(), ct);
        var dailyHoursByEngineer = await _timeEntries.GetDailyHoursByEngineerInRangeAsync(weekStart, weekEnd, scopedIds.ToList(), ct);
        var dayCount = weekEnd.DayNumber - weekStart.DayNumber + 1;
        var weekDates = Enumerable.Range(0, dayCount).Select(weekStart.AddDays).ToList();
        var nameById = allActive.ToDictionary(e => e.Id, e => e.Name);

        var entries = scopedIds
            .Select(id => new EngineerHoursDto(
                id,
                nameById.GetValueOrDefault(id, "Unknown"),
                hoursByEngineer.GetValueOrDefault(id, 0m),
                weekDates.Select(d => new DailyHoursDto(d, dailyHoursByEngineer.GetValueOrDefault((id, d), 0m))).ToList()))
            .OrderBy(e => e.Name)
            .ToList();

        var hoursByProject = await _timeEntries.GetHoursByProjectInRangeAsync(weekStart, weekEnd, scopedIds.ToList(), ct);
        var projectIds = hoursByProject.Keys.Where(id => id != Guid.Empty).ToList();
        var projectNameById = await _projects.GetNamesByIdsAsync(projectIds, ct);

        // Hours on anyone's personal tasks (see Project.PersonalOwnerId) are folded into one anonymous
        // line, so a person's private project never shows up by name in the breakdown.
        var personalIds = await _projects.GetPersonalProjectIdsAsync(projectIds, ct);

        // Executive/HR/Accountant are filtered out of personal tasks by row-level security, so hours on a task
        // they can't load fall into "no project" (General). Nothing but a personal task is hidden from them,
        // so move those hours to the Personal tasks line, where everyone else already sees them.
        var hiddenPersonalHours = 0m;
        if (Roles.IsOrgReadOnlyViewer(request.CallerRole))
        {
            var noProjectRows = await _timeEntries.GetEntriesByProjectInRangeAsync(weekStart, weekEnd, scopedIds.ToList(), null, ct);
            hiddenPersonalHours = (noProjectRows ?? []).Where(r => r.Entry.TaskId.HasValue && r.Task is null).Sum(r => r.Entry.Hours);
        }

        var projectEntries = hoursByProject
            .Where(kv => !personalIds.Contains(kv.Key))
            .Select(kv => new ProjectHoursDto(
                kv.Key == Guid.Empty ? null : kv.Key,
                kv.Key == Guid.Empty ? "General" : projectNameById.GetValueOrDefault(kv.Key, "Unknown"),
                kv.Key == Guid.Empty ? kv.Value - hiddenPersonalHours : kv.Value,
                kv.Key == Guid.Empty ? "general" : "project"))
            .Where(p => p.TotalHours > 0 || p.Kind != "general")
            .ToList();
        var personalHours = hoursByProject.Where(kv => personalIds.Contains(kv.Key)).Sum(kv => kv.Value) + hiddenPersonalHours;
        if (personalHours > 0)
            projectEntries.Add(new ProjectHoursDto(null, "Personal tasks", personalHours, "personal"));
        projectEntries = projectEntries.OrderBy(p => p.ProjectName).ToList();

        return ServiceResult<TimeEntrySummaryDto>.Ok(
            new TimeEntrySummaryDto(weekStart.ToString("yyyy-MM-dd"), weekEnd.ToString("yyyy-MM-dd"), entries, projectEntries, entries.Sum(e => e.TotalHours)));
    }
}
