using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Projects.Queries;

/// <param name="PointsDelivered">Points on tasks finished that week (a task counts only when it is Done).</param>
/// <param name="PointsEarned">Points earned that week as work moved forward, including parts of tasks not yet finished; negative parts when work was taken back.</param>
public record ThroughputWeekDto(DateOnly WeekOf, int PointsDelivered, decimal PointsEarned = 0m);

public record GetProjectThroughputQuery(Guid ProjectId, Guid ActorId, string ActorRole) : IRequest<ServiceResult<IReadOnlyList<ThroughputWeekDto>>>;

/// <summary>Returns delivered story points per week over the 6-week rolling window ending today.</summary>
public class GetProjectThroughputHandler : IRequestHandler<GetProjectThroughputQuery, ServiceResult<IReadOnlyList<ThroughputWeekDto>>>
{
    private readonly IProjectRepository _projects;
    private readonly IProjectAccessPolicy _access;

    public GetProjectThroughputHandler(IProjectRepository projects, IProjectAccessPolicy access)
    {
        _projects = projects;
        _access = access;
    }

    public async Task<ServiceResult<IReadOnlyList<ThroughputWeekDto>>> Handle(GetProjectThroughputQuery query, CancellationToken ct)
    {
        var project = await _projects.GetByIdAsync(query.ProjectId, ct);
        if (project is null)
            return ServiceResult<IReadOnlyList<ThroughputWeekDto>>.Fail("NOT_FOUND", "Project not found.");

        if (!Roles.IsOrgReadOnlyViewer(query.ActorRole) && !await _access.CanAccessProjectAsync(project.Id, query.ActorId, query.ActorRole, ct))
            return ServiceResult<IReadOnlyList<ThroughputWeekDto>>.Fail("FORBIDDEN", "You do not have access to this project.");

        var points = await _projects.GetWeeklyThroughputAsync(query.ProjectId, ct);
        var earned = (await _projects.GetWeeklyEarnedAsync(query.ProjectId, ct)).ToDictionary(e => e.WeekOf, e => e.PointsEarned);
        var delivered = points.ToDictionary(p => p.WeekOf, p => p.PointsDelivered);
        // A week with only earned points (work moved but nothing finished) is still a week worth showing.
        var result = delivered.Keys.Union(earned.Keys).OrderBy(w => w)
            .Select(w => new ThroughputWeekDto(w, delivered.GetValueOrDefault(w), earned.GetValueOrDefault(w))).ToList();

        return ServiceResult<IReadOnlyList<ThroughputWeekDto>>.Ok(result);
    }
}
