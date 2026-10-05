using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Projects.Queries;

public record ListMyProjectsQuery(Guid UserId) : IRequest<ServiceResult<IReadOnlyList<MyProjectDto>>>;

public class ListMyProjectsHandler : IRequestHandler<ListMyProjectsQuery, ServiceResult<IReadOnlyList<MyProjectDto>>>
{
    private readonly IProjectRepository _projects;

    public ListMyProjectsHandler(IProjectRepository projects) => _projects = projects;

    public async Task<ServiceResult<IReadOnlyList<MyProjectDto>>> Handle(ListMyProjectsQuery query, CancellationToken ct)
    {
        var projects = await _projects.ListMyProjectsAsync(query.UserId, ct);
        return ServiceResult<IReadOnlyList<MyProjectDto>>.Ok(projects);
    }
}
