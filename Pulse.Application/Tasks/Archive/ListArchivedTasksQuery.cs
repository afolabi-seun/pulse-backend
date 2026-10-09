using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Tasks.Archive;

public record ListArchivedTasksQuery(Guid? ProjectId, string? Search, int Page = 1, int PageSize = 25) : IRequest<ServiceResult<ArchivedTaskPage>>;

public class ListArchivedTasksHandler : IRequestHandler<ListArchivedTasksQuery, ServiceResult<ArchivedTaskPage>>
{
    private readonly ITaskRepository _tasks;
    private readonly IProjectRepository _projects;
    private readonly IEngineerRepository _engineers;

    public ListArchivedTasksHandler(ITaskRepository tasks, IProjectRepository projects, IEngineerRepository engineers)
    {
        _tasks = tasks;
        _projects = projects;
        _engineers = engineers;
    }

    public async Task<ServiceResult<ArchivedTaskPage>> Handle(ListArchivedTasksQuery query, CancellationToken ct)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        var (items, total) = await _tasks.ListArchivedAsync(query.ProjectId, query.Search, (page - 1) * pageSize, pageSize, ct);

        var projectIds = items.Select(t => t.ProjectId).Distinct().ToList();
        var names = await _projects.GetNamesByIdsAsync(projectIds, ct);
        var codes = await _projects.GetCodesByIdsAsync(projectIds, ct);
        var peopleIds = items.SelectMany(t => new[] { t.AssigneeId, t.ArchivedById }).Where(i => i.HasValue).Select(i => i!.Value).Distinct().ToList();
        var people = peopleIds.Count == 0 ? [] : await _engineers.GetByIdsAsync(peopleIds, ct);
        var personName = people.ToDictionary(e => e.Id, e => e.Name);

        var dtos = items.Select(t => new ArchivedTaskDto(
            t.Id,
            codes.TryGetValue(t.ProjectId, out var code) && !string.IsNullOrEmpty(code) ? $"{code}-{t.TaskNumber}" : $"#{t.TaskNumber}",
            t.Title, t.Status.ToString(), t.ProjectId, names.GetValueOrDefault(t.ProjectId, "Unknown project"),
            t.AssigneeId.HasValue ? personName.GetValueOrDefault(t.AssigneeId.Value) : null,
            t.ParentTaskId.HasValue, t.ArchivedAt!.Value,
            t.ArchivedById.HasValue ? personName.GetValueOrDefault(t.ArchivedById.Value) : null,
            t.ArchiveReason)).ToList();

        return ServiceResult<ArchivedTaskPage>.Ok(new ArchivedTaskPage(dtos, total, page, pageSize));
    }
}
