using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Retrospectives;

public record GetRetroQuery(Guid SprintId, Guid ActorId, string ActorRole) : IRequest<ServiceResult<RetroDto?>>;

public class GetRetroHandler : IRequestHandler<GetRetroQuery, ServiceResult<RetroDto?>>
{
    private readonly IRetroRepository _retros;
    private readonly ISprintRepository _sprints;
    private readonly IProjectAccessPolicy _access;

    public GetRetroHandler(IRetroRepository retros, ISprintRepository sprints, IProjectAccessPolicy access)
    {
        _retros = retros;
        _sprints = sprints;
        _access = access;
    }

    public async Task<ServiceResult<RetroDto?>> Handle(GetRetroQuery request, CancellationToken ct)
    {
        var sprint = await _sprints.GetByIdAsync(request.SprintId, ct);
        if (sprint is null)
            return ServiceResult<RetroDto?>.Ok(null);

        if (!Roles.IsOrgReadOnlyViewer(request.ActorRole) && !await _access.CanAccessSprintAsync(sprint.Id, sprint.TeamId, request.ActorId, request.ActorRole, ct))
            return ServiceResult<RetroDto?>.Fail("FORBIDDEN", "You do not have access to this sprint.");

        var retro = await _retros.GetBySprintAsync(request.SprintId, ct);
        return ServiceResult<RetroDto?>.Ok(retro is null ? null : RetroDto.From(retro));
    }
}
