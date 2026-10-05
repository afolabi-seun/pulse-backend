using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Epics.Queries;

public record GetEpicQuery(Guid Id, Guid ActorId, string ActorRole) : IRequest<ServiceResult<EpicDto>>;

public class GetEpicHandler : IRequestHandler<GetEpicQuery, ServiceResult<EpicDto>>
{
    private readonly IEpicRepository _epics;
    private readonly ITaskRepository _tasks;
    private readonly IProjectAccessPolicy _access;

    public GetEpicHandler(IEpicRepository epics, ITaskRepository tasks, IProjectAccessPolicy access)
    {
        _epics = epics;
        _tasks = tasks;
        _access = access;
    }

    public async Task<ServiceResult<EpicDto>> Handle(GetEpicQuery query, CancellationToken ct)
    {
        var epic = await _epics.GetByIdAsync(query.Id, ct);
        if (epic is null) return ServiceResult<EpicDto>.Fail("NOT_FOUND", "Epic not found.");

        if (!Roles.IsOrgReadOnlyViewer(query.ActorRole) && !await _access.CanAccessProjectAsync(epic.ProjectId, query.ActorId, query.ActorRole, ct))
            return ServiceResult<EpicDto>.Fail("FORBIDDEN", "You do not have access to this epic.");

        var progress = await _tasks.GetTaskProgressByEpicsAsync([epic.Id], ct);
        progress.TryGetValue(epic.Id, out var counts);
        return ServiceResult<EpicDto>.Ok(EpicDto.From(epic, counts.Total, counts.Completed));
    }
}
