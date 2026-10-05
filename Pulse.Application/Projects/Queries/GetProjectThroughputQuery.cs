using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Projects.Queries;

public record ThroughputWeekDto(DateOnly WeekOf, int PointsDelivered);

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
        var result = points.Select(p => new ThroughputWeekDto(p.WeekOf, p.PointsDelivered)).ToList();

        return ServiceResult<IReadOnlyList<ThroughputWeekDto>>.Ok(result);
    }
}
