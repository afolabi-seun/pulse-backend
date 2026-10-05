using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Epics.Commands;

public record DeleteEpicCommand(Guid Id, Guid ActorId, string? IpAddress, string ActorRole = "") : IRequest<ServiceResult<bool>>;

public class DeleteEpicHandler : IRequestHandler<DeleteEpicCommand, ServiceResult<bool>>
{
    private readonly IEpicRepository _epics;
    private readonly ITaskRepository _tasks;
    private readonly IAuditLogRepository _audit;
    private readonly IProjectAccessPolicy _access;

    public DeleteEpicHandler(IEpicRepository epics, ITaskRepository tasks, IAuditLogRepository audit, IProjectAccessPolicy access)
    {
        _epics = epics;
        _tasks = tasks;
        _audit = audit;
        _access = access;
    }

    public async Task<ServiceResult<bool>> Handle(DeleteEpicCommand cmd, CancellationToken ct)
    {
        var epic = await _epics.GetByIdAsync(cmd.Id, ct);
        if (epic is null) return ServiceResult<bool>.Fail("NOT_FOUND", "Epic not found.");

        if (!await _access.CanAccessProjectAsync(epic.ProjectId, cmd.ActorId, cmd.ActorRole, ct))
            return ServiceResult<bool>.Fail("FORBIDDEN", "You do not have access to this epic.");

        _epics.Remove(epic);
        await _epics.SaveChangesAsync(ct);

        await _audit.LogAsync("EPIC_DELETED", cmd.ActorId, cmd.IpAddress,
            $"Deleted epic {cmd.Id} '{epic.Title}'", ct);

        return ServiceResult<bool>.Ok(true);
    }
}
