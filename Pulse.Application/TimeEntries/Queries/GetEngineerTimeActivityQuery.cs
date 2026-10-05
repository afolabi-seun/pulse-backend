using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.TimeEntries.Queries;

public record ActivityEntryDto(DateOnly Date, decimal Hours, string? Note);

/// <param name="Status">camelCase task status ("active", "inQa", …); null for a task the caller can't see.</param>
public record ActivityTaskDto(
    Guid TaskId, string? Key, string Title, string? ProjectName, string? Status, DateOnly? DueDate,
    decimal Hours, IReadOnlyList<ActivityEntryDto> Entries);

public record CategoryHoursDto(string Category, decimal Hours);

/// <summary>What one engineer is assigned to next to what they actually logged time on, for a period.</summary>
/// <param name="Assigned">Every task currently assigned to them that isn't Done (Backlog included), each with the
/// hours they logged on it in the period — zero when none, which is the gap this view exists to surface.</param>
/// <param name="LoggedOnly">Tasks they logged time on in the period that are NOT currently assigned to them —
/// finished, reassigned, loaned or handed-off work — so hours on work that has moved on still show.</param>
/// <param name="OtherTime">Meeting / admin / leave / other hours.</param>
public record EngineerTimeActivityDto(
    IReadOnlyList<ActivityTaskDto> Assigned,
    IReadOnlyList<ActivityTaskDto> LoggedOnly,
    IReadOnlyList<CategoryHoursDto> OtherTime,
    int AssignedWithoutTime);

public record GetEngineerTimeActivityQuery(
    Guid ActorId, string ActorRole, Guid EngineerId, DateOnly From, DateOnly To)
    : IRequest<ServiceResult<EngineerTimeActivityDto>>;

public class GetEngineerTimeActivityHandler : IRequestHandler<GetEngineerTimeActivityQuery, ServiceResult<EngineerTimeActivityDto>>
{
    private const int MaxRangeDays = 62; // matches the Time Summary's own cap
    private const string PersonalTaskLabel = "Personal task";

    private readonly ITimeEntryRepository _timeEntries;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;
    private readonly ITaskRepository _tasks;
    private readonly IProjectRepository _projects;

    public GetEngineerTimeActivityHandler(ITimeEntryRepository timeEntries, IEngineerRepository engineers,
        ITeamRepository teams, ITaskRepository tasks, IProjectRepository projects)
    {
        _timeEntries = timeEntries;
        _engineers = engineers;
        _teams = teams;
        _tasks = tasks;
        _projects = projects;
    }

    public async Task<ServiceResult<EngineerTimeActivityDto>> Handle(GetEngineerTimeActivityQuery query, CancellationToken ct)
    {
        if (query.To < query.From)
            return ServiceResult<EngineerTimeActivityDto>.Fail("VALIDATION_ERROR", "'to' must be on or after 'from'.");
        if (query.To.DayNumber - query.From.DayNumber > MaxRangeDays)
            return ServiceResult<EngineerTimeActivityDto>.Fail("VALIDATION_ERROR", $"Range cannot exceed {MaxRangeDays} days.");

        // Same visibility as the engineer's time entries themselves.
        if (!await TimeEntryVisibility.CanViewAsync(query.ActorId, query.ActorRole, query.EngineerId, _engineers, _teams, ct))
            return ServiceResult<EngineerTimeActivityDto>.Fail("FORBIDDEN", "You do not have access to this engineer's time.");

        var entries = await _timeEntries.GetByEngineerAndDateRangeAsync(query.EngineerId, query.From, query.To, ct);
        var assigned = await _tasks.GetActiveByAssigneeAsync(query.EngineerId, ct);

        var loggedTaskIds = entries.Where(e => e.TaskId.HasValue).Select(e => e.TaskId!.Value).Distinct().ToList();
        var assignedIds = assigned.Select(t => t.Id).ToHashSet();
        var loggedOnlyIds = loggedTaskIds.Where(id => !assignedIds.Contains(id)).ToList();
        var loggedOnlyTasks = loggedOnlyIds.Count == 0
            ? new Dictionary<Guid, Domain.Tasks.PulseTask>()
            : (await _tasks.GetByIdsAsync(loggedOnlyIds, ct)).ToDictionary(t => t.Id);

        // Executive/HR/Accountant are filtered out of personal tasks by row-level security, so a task they
        // can't load here that the engineer logged time on is a personal one (nothing else is hidden from
        // them). Only the owner can log time on a personal task, so it is the engineer's own, still-open to-do:
        // show it as "Personal task" among their assigned work, the way PMO sees it — not as an unnamed task
        // the engineer is no longer assigned to.
        var hiddenPersonalIds = Roles.IsOrgReadOnlyViewer(query.ActorRole)
            ? loggedOnlyIds.Where(id => !loggedOnlyTasks.ContainsKey(id)).ToHashSet()
            : [];

        var allTasks = assigned.Concat(loggedOnlyTasks.Values).ToList();
        var projectIds = allTasks.Select(t => t.ProjectId).Distinct().ToList();
        var names = await _projects.GetNamesByIdsAsync(projectIds, ct);
        var codes = await _projects.GetCodesByIdsAsync(projectIds, ct);
        var personal = await _projects.GetPersonalProjectIdsAsync(projectIds, ct);

        var entriesByTask = entries
            .Where(e => e.TaskId.HasValue)
            .GroupBy(e => e.TaskId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Date).ToList());

        // A personal task (see Project.PersonalOwnerId) is shown to anyone but its owner as just "Personal
        // task" — no key, project or due date. The owner is the engineer whose time this is.
        bool Hidden(Domain.Tasks.PulseTask t) => personal.Contains(t.ProjectId) && query.ActorId != query.EngineerId;

        ActivityTaskDto ToDto(Domain.Tasks.PulseTask t)
        {
            entriesByTask.TryGetValue(t.Id, out var taskEntries);
            var list = (taskEntries ?? []).Select(e => new ActivityEntryDto(e.Date, e.Hours, e.Note)).ToList();
            var hours = list.Sum(e => e.Hours);
            if (Hidden(t))
                return new ActivityTaskDto(t.Id, null, PersonalTaskLabel, "Personal tasks", Camel(t.Status.ToString()), null, hours, list);

            var key = codes.TryGetValue(t.ProjectId, out var code) ? $"{code}-{t.TaskNumber}" : null;
            return new ActivityTaskDto(t.Id, key, t.Title, names.GetValueOrDefault(t.ProjectId), Camel(t.Status.ToString()), t.DueDate, hours, list);
        }

        // The gaps first (assigned, nothing logged), then by due date — soonest first, undated last.
        var assignedDtos = assigned.Select(ToDto)
            .Concat(hiddenPersonalIds.Select(id =>
            {
                var list = entriesByTask[id].Select(e => new ActivityEntryDto(e.Date, e.Hours, e.Note)).ToList();
                return new ActivityTaskDto(id, null, PersonalTaskLabel, "Personal tasks", null, null, list.Sum(e => e.Hours), list);
            }))
            .OrderBy(d => d.Hours > 0 ? 1 : 0)
            .ThenBy(d => d.DueDate ?? DateOnly.MaxValue)
            .ThenBy(d => d.Title)
            .ToList();

        var loggedOnlyDtos = loggedOnlyIds
            .Where(id => !hiddenPersonalIds.Contains(id))
            .Select(id => loggedOnlyTasks.TryGetValue(id, out var t)
                ? ToDto(t)
                // Not visible to the caller (e.g. a task hidden by row-level security): hours only, no name.
                : new ActivityTaskDto(id, null, "Private task", null, null, null,
                    entriesByTask[id].Sum(e => e.Hours),
                    entriesByTask[id].Select(e => new ActivityEntryDto(e.Date, e.Hours, e.Note)).ToList()))
            .OrderByDescending(d => d.Hours)
            .ToList();

        var otherTime = entries
            .Where(e => !e.TaskId.HasValue)
            .GroupBy(e => e.Category)
            .Select(g => new CategoryHoursDto(Camel(g.Key.ToString()), g.Sum(e => e.Hours)))
            .OrderByDescending(c => c.Hours)
            .ToList();

        return ServiceResult<EngineerTimeActivityDto>.Ok(new EngineerTimeActivityDto(
            assignedDtos, loggedOnlyDtos, otherTime, assignedDtos.Count(d => d.Hours == 0)));
    }

    private static string Camel(string s) => s.Length == 0 ? s : char.ToLower(s[0]) + s[1..];
}
