using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Epics.Queries;

public record ListEpicsQuery(Guid ProjectId, Guid ActorId, string ActorRole, bool BacklogOnly = false) : IRequest<ServiceResult<IReadOnlyList<EpicDto>>>;

public class ListEpicsHandler : IRequestHandler<ListEpicsQuery, ServiceResult<IReadOnlyList<EpicDto>>>
{
    private readonly IEpicRepository _epics;
    private readonly ITaskRepository _tasks;
    private readonly IProjectAccessPolicy _access;

    public ListEpicsHandler(IEpicRepository epics, ITaskRepository tasks, IProjectAccessPolicy access)
    {
        _epics = epics;
        _tasks = tasks;
        _access = access;
    }

    public async Task<ServiceResult<IReadOnlyList<EpicDto>>> Handle(ListEpicsQuery query, CancellationToken ct)
    {
        if (!Roles.IsOrgReadOnlyViewer(query.ActorRole) && !await _access.CanAccessProjectAsync(query.ProjectId, query.ActorId, query.ActorRole, ct))
            return ServiceResult<IReadOnlyList<EpicDto>>.Fail("FORBIDDEN", "You do not have access to this project.");

        var epics = query.BacklogOnly
            ? await _epics.ListBacklogByProjectAsync(query.ProjectId, ct)
            : await _epics.ListByProjectAsync(query.ProjectId, ct);

        var epicIds = epics.Select(e => e.Id).ToList();
        var progress = await _tasks.GetTaskProgressByEpicsAsync(epicIds, ct);

        var dtos = epics.Select(e =>
        {
            progress.TryGetValue(e.Id, out var counts);
            return EpicDto.From(e, counts.Total, counts.Completed);
        }).ToList();

        return ServiceResult<IReadOnlyList<EpicDto>>.Ok(dtos);
    }
}
