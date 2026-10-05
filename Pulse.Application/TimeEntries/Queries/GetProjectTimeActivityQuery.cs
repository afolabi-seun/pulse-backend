using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.TimeEntries.Queries;

public record ProjectActivityPersonDto(Guid EngineerId, string Name, decimal Hours);

/// <param name="TaskId">Null for a category row (meetings, admin, leave, other) — time with no task.</param>
/// <param name="Label">The task's title, or the category's name for a category row.</param>
public record ProjectActivityItemDto(
    Guid? TaskId, string Label, string? Key, string? Status, decimal Hours, IReadOnlyList<ProjectActivityPersonDto> People);

public record ProjectActivityByPersonDto(Guid EngineerId, string Name, int Tasks, decimal Hours);

/// <summary>Who logged the hours behind one "Hours by project" line, and on what.</summary>
/// <param name="Items">One row per task (and per non-task category), biggest first. Empty for the personal-tasks line,
/// which names people but never titles.</param>
public record ProjectTimeActivityDto(
    string Kind, string Name, decimal TotalHours,
    IReadOnlyList<ProjectActivityItemDto> Items, IReadOnlyList<ProjectActivityByPersonDto> People);

/// <param name="Kind">"project" (needs <paramref name="ProjectId"/>) | "general" | "personal"</param>
public record GetProjectTimeActivityQuery(
    Guid ActorId, string ActorRole, string Kind, Guid? ProjectId, DateOnly From, DateOnly To)
    : IRequest<ServiceResult<ProjectTimeActivityDto>>;

public class GetProjectTimeActivityHandler : IRequestHandler<GetProjectTimeActivityQuery, ServiceResult<ProjectTimeActivityDto>>
{
    private const int MaxRangeDays = 62; // matches the Time Summary's own cap

    private static readonly Dictionary<string, string> CategoryLabel = new()
    {
        ["task"] = "Task time", ["meeting"] = "Meetings", ["admin"] = "Admin", ["leave"] = "Leave", ["other"] = "Other",
    };

    private readonly ITimeEntryRepository _timeEntries;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;
    private readonly IProjectRepository _projects;

    public GetProjectTimeActivityHandler(ITimeEntryRepository timeEntries, IEngineerRepository engineers,
        ITeamRepository teams, IProjectRepository projects)
    {
        _timeEntries = timeEntries;
        _engineers = engineers;
        _teams = teams;
        _projects = projects;
    }

    public async Task<ServiceResult<ProjectTimeActivityDto>> Handle(GetProjectTimeActivityQuery query, CancellationToken ct)
    {
        var kind = query.Kind?.ToLowerInvariant();
        if (kind is not ("project" or "general" or "personal"))
            return ServiceResult<ProjectTimeActivityDto>.Fail("VALIDATION_ERROR", "Kind must be 'project', 'general' or 'personal'.");
        if (kind == "project" && (query.ProjectId is null || query.ProjectId == Guid.Empty))
            return ServiceResult<ProjectTimeActivityDto>.Fail("VALIDATION_ERROR", "projectId is required for a project.");
        if (query.To < query.From)
            return ServiceResult<ProjectTimeActivityDto>.Fail("VALIDATION_ERROR", "'to' must be on or after 'from'.");
        if (query.To.DayNumber - query.From.DayNumber > MaxRangeDays)
            return ServiceResult<ProjectTimeActivityDto>.Fail("VALIDATION_ERROR", $"Range cannot exceed {MaxRangeDays} days.");

        // Same engineers as the summary's totals, so what is expanded adds up to the line it was opened from.
        var (allActive, scopedIds) = await TimeSummaryScope.ResolveAsync(query.ActorRole, query.ActorId, _engineers, _teams, ct);
        var scoped = scopedIds.ToList();
        var nameById = allActive.ToDictionary(e => e.Id, e => e.Name);

        string name;
        IReadOnlyList<Guid>? projectIds;
        var titlesHidden = false; // the personal line names people, never tasks

        if (kind == "general")
        {
            name = "General";
            projectIds = null;
        }
        else if (kind == "personal")
        {
            name = "Personal tasks";
            titlesHidden = true;
            // Every personal project that has hours in the window, among the engineers in scope.
            var hoursByProject = await _timeEntries.GetHoursByProjectInRangeAsync(query.From, query.To, scoped, ct);
            var candidates = hoursByProject.Keys.Where(id => id != Guid.Empty).ToList();
            projectIds = (await _projects.GetPersonalProjectIdsAsync(candidates, ct)).ToList();
        }
        else
        {
            var project = await _projects.GetByIdAsync(query.ProjectId!.Value, ct);
            if (project is null)
                return ServiceResult<ProjectTimeActivityDto>.Fail("NOT_FOUND", "Project not found.");
            // Asking for a personal project by id must not name its tasks any more than the personal line does.
            if (project.PersonalOwnerId is not null)
            {
                name = "Personal tasks";
                titlesHidden = true;
            }
            else
            {
                name = project.Name;
            }
            projectIds = [project.Id];
        }

        var rows = projectIds is { Count: 0 }
            ? []
            : await _timeEntries.GetEntriesByProjectInRangeAsync(query.From, query.To, scoped, projectIds, ct);

        // Executive/HR/Accountant are filtered out of personal tasks by row-level security, so time on one
        // shows up among the "no project" rows with no task attached. Nothing else is hidden from them, so
        // it belongs to the Personal tasks line, not General (which is where the summary used to leave it).
        if (Roles.IsOrgReadOnlyViewer(query.ActorRole) && (kind is "general" or "personal"))
        {
            var noProject = kind == "general"
                ? rows
                : await _timeEntries.GetEntriesByProjectInRangeAsync(query.From, query.To, scoped, null, ct);
            var hidden = noProject.Where(r => r.Entry.TaskId.HasValue && r.Task is null).ToList();
            rows = kind == "general"
                ? rows.Where(r => !hidden.Contains(r)).ToList()
                : rows.Concat(hidden).ToList();
        }

        // The project's code, for a task's display key.
        var codes = rows.Where(r => r.Task is not null).Select(r => r.Task!.ProjectId).Distinct().ToList() is { Count: > 0 } ids
            ? await _projects.GetCodesByIdsAsync(ids, ct)
            : new Dictionary<Guid, string>();

        string PersonName(Guid id) => nameById.GetValueOrDefault(id, "Unknown");
        string Camel(string s) => s.Length == 0 ? s : char.ToLower(s[0]) + s[1..];

        var items = new List<ProjectActivityItemDto>();
        if (!titlesHidden)
        {
            // One row per task, then one per non-task category, each with who logged how much.
            foreach (var group in rows.GroupBy(r => r.Entry.TaskId.HasValue ? (object)r.Entry.TaskId.Value : r.Entry.Category))
            {
                var first = group.First();
                var people = group.GroupBy(r => r.Entry.EngineerId)
                    .Select(g => new ProjectActivityPersonDto(g.Key, PersonName(g.Key), g.Sum(r => r.Entry.Hours)))
                    .OrderByDescending(p => p.Hours).ThenBy(p => p.Name).ToList();

                if (first.Entry.TaskId is Guid taskId)
                {
                    var task = first.Task;
                    var key = task is not null && codes.TryGetValue(task.ProjectId, out var code) ? $"{code}-{task.TaskNumber}" : null;
                    items.Add(new ProjectActivityItemDto(taskId, task?.Title ?? "Private task", key,
                        task is null ? null : Camel(task.Status.ToString()), people.Sum(p => p.Hours), people));
                }
                else
                {
                    var category = Camel(first.Entry.Category.ToString());
                    items.Add(new ProjectActivityItemDto(null, CategoryLabel.GetValueOrDefault(category, category), null, null,
                        people.Sum(p => p.Hours), people));
                }
            }
            items = items.OrderByDescending(i => i.Hours).ThenBy(i => i.Label).ToList();
        }

        var byPerson = rows.GroupBy(r => r.Entry.EngineerId)
            .Select(g => new ProjectActivityByPersonDto(
                g.Key, PersonName(g.Key),
                g.Where(r => r.Entry.TaskId.HasValue).Select(r => r.Entry.TaskId!.Value).Distinct().Count(),
                g.Sum(r => r.Entry.Hours)))
            .OrderByDescending(p => p.Hours).ThenBy(p => p.Name).ToList();

        return ServiceResult<ProjectTimeActivityDto>.Ok(
            new ProjectTimeActivityDto(kind, name, byPerson.Sum(p => p.Hours), items, byPerson));
    }
}
