using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Search;

public record SearchQuery(string Q, Guid ActorId, string ActorRole) : IRequest<ServiceResult<SearchResultDto>>;

public class SearchHandler : IRequestHandler<SearchQuery, ServiceResult<SearchResultDto>>
{
    private readonly ITaskRepository _tasks;
    private readonly IEngineerRepository _engineers;
    private readonly IProjectRepository _projects;
    private readonly IProjectAccessPolicy _access;

    public SearchHandler(ITaskRepository tasks, IEngineerRepository engineers, IProjectRepository projects, IProjectAccessPolicy access)
    {
        _tasks     = tasks;
        _engineers = engineers;
        _projects  = projects;
        _access    = access;
    }

    public async Task<ServiceResult<SearchResultDto>> Handle(SearchQuery request, CancellationToken ct)
    {
        var q = request.Q?.Trim() ?? string.Empty;
        if (q.Length < 2)
            return ServiceResult<SearchResultDto>.Ok(new SearchResultDto([], []));

        // Over-fetch, then drop tasks the caller can't access, and return up to 8.
        var candidates = await _tasks.SearchAsync(q, 30, request.ActorId, ct);
        var accessible = new List<Domain.Tasks.PulseTask>();
        foreach (var t in candidates)
        {
            if (accessible.Count >= 8) break;
            if (t.AssigneeId == request.ActorId
                || await _access.CanAccessProjectAsync(t.ProjectId, request.ActorId, request.ActorRole, ct))
                accessible.Add(t);
        }

        var projectIds = accessible.Select(t => t.ProjectId).Distinct().ToList();
        var projectCodes = projectIds.Count > 0
            ? await _projects.GetCodesByIdsAsync(projectIds, ct)
            : new Dictionary<Guid, string>();
        var taskHits = accessible
            .Select(t => new TaskHitDto(t.Id, t.Title, t.Points, t.Status.ToString(),
                projectCodes.TryGetValue(t.ProjectId, out var code) ? $"{code}-{t.TaskNumber}" : null))
            .ToList();

        // Engineer (people) results are directory data, not tenant-scoped.
        var engineers = await _engineers.SearchAsync(q, 5, ct);
        var engineerHits = engineers
            .Select(e => new EngineerHitDto(e.Id, e.Name, e.Email, e.Role))
            .ToList();

        return ServiceResult<SearchResultDto>.Ok(new SearchResultDto(taskHits, engineerHits));
    }
}
