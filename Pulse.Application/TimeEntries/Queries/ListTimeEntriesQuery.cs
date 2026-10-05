using System.Text;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.TimeEntries.Queries;

public record ListTimeEntriesQuery(
    Guid ActorId,
    string ActorRole,
    Guid? EngineerId,
    DateOnly? From,
    DateOnly? To,
    int Limit,
    string? Cursor) : IRequest<ServiceResult<PagedResultDto<TimeEntryDto>>>;

public class ListTimeEntriesHandler : IRequestHandler<ListTimeEntriesQuery, ServiceResult<PagedResultDto<TimeEntryDto>>>
{
    private readonly ITimeEntryRepository _timeEntries;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;
    private readonly ITaskRepository _tasks;
    private readonly IProjectRepository _projects;

    public ListTimeEntriesHandler(ITimeEntryRepository timeEntries, IEngineerRepository engineers, ITeamRepository teams, ITaskRepository tasks, IProjectRepository projects)
    {
        _projects = projects;
        _timeEntries = timeEntries;
        _engineers = engineers;
        _teams = teams;
        _tasks = tasks;
    }

    public async Task<ServiceResult<PagedResultDto<TimeEntryDto>>> Handle(ListTimeEntriesQuery query, CancellationToken ct)
    {
        // Individual contributors can only view their own time entries
        var targetEngineerId = query.ActorRole is Roles.Engineer or Roles.Designer
            ? query.ActorId
            : query.EngineerId ?? query.ActorId;

        if (!await TimeEntryVisibility.CanViewAsync(query.ActorId, query.ActorRole, targetEngineerId, _engineers, _teams, ct))
            return ServiceResult<PagedResultDto<TimeEntryDto>>.Fail("FORBIDDEN", "You do not have access to this engineer's time entries.");

        if (query.From.HasValue && query.To.HasValue)
        {
            var range = await _timeEntries.GetByEngineerAndDateRangeAsync(targetEngineerId, query.From.Value, query.To.Value, ct);
            return ServiceResult<PagedResultDto<TimeEntryDto>>.Ok(
                new PagedResultDto<TimeEntryDto>(await ToDtosAsync(range, query.ActorId, ct), null, false));
        }

        var limit = Math.Clamp(query.Limit, 1, 100);
        var raw = await _timeEntries.GetByEngineerAsync(targetEngineerId, limit + 1, query.Cursor, ct);

        var hasMore = raw.Count > limit;
        var page = hasMore ? raw.Take(limit).ToList() : raw.ToList();

        string? nextCursor = null;
        if (hasMore)
        {
            var last = page[^1];
            nextCursor = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{last.Date:O}|{last.Id}"));
        }

        return ServiceResult<PagedResultDto<TimeEntryDto>>.Ok(
            new PagedResultDto<TimeEntryDto>(await ToDtosAsync(page, query.ActorId, ct), nextCursor, hasMore));
    }

    /// <summary>Resolves each entry's task title, display key and project name independently of the
    /// viewer's own task list — an entry is a historical record. Time logged on someone's personal task
    /// (see Project.PersonalOwnerId) is shown to anyone but its owner as just "Personal task", so a
    /// timesheet review never reveals a private to-do's title or its project's name.</summary>
    private async Task<List<TimeEntryDto>> ToDtosAsync(IReadOnlyList<Domain.TimeEntries.TimeEntry> entries, Guid viewerId, CancellationToken ct)
    {
        var taskIds = entries.Where(e => e.TaskId.HasValue).Select(e => e.TaskId!.Value).Distinct().ToList();
        var taskById = taskIds.Count == 0
            ? new Dictionary<Guid, Domain.Tasks.PulseTask>()
            : (await _tasks.GetByIdsAsync(taskIds, ct)).ToDictionary(t => t.Id);

        Guid? ProjectOf(Domain.TimeEntries.TimeEntry e) =>
            e.ProjectId ?? (e.TaskId.HasValue && taskById.TryGetValue(e.TaskId.Value, out var t) ? t.ProjectId : null);

        var projectIds = entries.Select(ProjectOf).Where(id => id.HasValue && id.Value != Guid.Empty).Select(id => id!.Value).Distinct().ToList();
        var names = projectIds.Count == 0 ? new Dictionary<Guid, string>() : await _projects.GetNamesByIdsAsync(projectIds, ct);
        var codes = projectIds.Count == 0 ? new Dictionary<Guid, string>() : await _projects.GetCodesByIdsAsync(projectIds, ct);
        var personal = await _projects.GetPersonalProjectIdsAsync(projectIds, ct);

        return entries.Select(e =>
        {
            var projectId = ProjectOf(e);
            taskById.TryGetValue(e.TaskId ?? Guid.Empty, out var task);

            if (projectId.HasValue && personal.Contains(projectId.Value) && e.EngineerId != viewerId)
                return TimeEntryDto.From(e, e.TaskId.HasValue ? "Personal task" : null, null, "Personal tasks");

            var key = task is not null && projectId.HasValue && codes.TryGetValue(projectId.Value, out var code)
                ? $"{code}-{task.TaskNumber}" : null;
            var projectName = projectId.HasValue && names.TryGetValue(projectId.Value, out var name) ? name : null;
            return TimeEntryDto.From(e, task?.Title, key, projectName);
        }).ToList();
    }
}
