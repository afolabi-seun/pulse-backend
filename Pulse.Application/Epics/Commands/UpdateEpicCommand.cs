using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Epics;
using MediatR;

namespace Pulse.Application.Epics.Commands;

public record UpdateEpicCommand(
    Guid Id,
    string? Title,
    string? Description,
    string? AcceptanceCriteria,
    string? Status,
    int? Order,
    Guid? SprintId,
    bool? RemoveFromSprint,
    Guid ActorId,
    string? IpAddress,
    string ActorRole = "") : IRequest<ServiceResult<EpicDto>>;

public class UpdateEpicHandler : IRequestHandler<UpdateEpicCommand, ServiceResult<EpicDto>>
{
    private readonly IEpicRepository _epics;
    private readonly IAuditLogRepository _audit;
    private readonly IProjectAccessPolicy _access;

    public UpdateEpicHandler(IEpicRepository epics, IAuditLogRepository audit, IProjectAccessPolicy access)
    {
        _epics = epics;
        _audit = audit;
        _access = access;
    }

    public async Task<ServiceResult<EpicDto>> Handle(UpdateEpicCommand cmd, CancellationToken ct)
    {
        var epic = await _epics.GetByIdAsync(cmd.Id, ct);
        if (epic is null) return ServiceResult<EpicDto>.Fail("NOT_FOUND", "Epic not found.");

        if (!await _access.CanAccessProjectAsync(epic.ProjectId, cmd.ActorId, cmd.ActorRole, ct))
            return ServiceResult<EpicDto>.Fail("FORBIDDEN", "You do not have access to this epic.");

        if (cmd.Title is not null || cmd.Order is not null || cmd.Description is not null || cmd.AcceptanceCriteria is not null)
            epic.Update(
                cmd.Title?.Trim() ?? epic.Title,
                cmd.Description is not null ? DescriptionSanitizer.Sanitize(cmd.Description) : epic.Description,
                cmd.AcceptanceCriteria is not null ? DescriptionSanitizer.Sanitize(cmd.AcceptanceCriteria) : epic.AcceptanceCriteria,
                cmd.Order ?? epic.Order);

        if (cmd.Status is not null && Enum.TryParse<EpicStatus>(cmd.Status, ignoreCase: true, out var status))
            epic.SetStatus(status);

        if (cmd.RemoveFromSprint == true)
            epic.RemoveFromSprint();
        else if (cmd.SprintId.HasValue)
            epic.AssignToSprint(cmd.SprintId.Value);

        await _epics.SaveChangesAsync(ct);
        await _audit.LogAsync("EPIC_UPDATED", cmd.ActorId, cmd.IpAddress,
            $"Updated epic {epic.Id} '{epic.Title}'", ct);

        return ServiceResult<EpicDto>.Ok(EpicDto.From(epic));
    }
}
