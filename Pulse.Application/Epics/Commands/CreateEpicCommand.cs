using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Epics;
using MediatR;

namespace Pulse.Application.Epics.Commands;

public record CreateEpicCommand(
    string Title,
    string? Description,
    string? AcceptanceCriteria,
    Guid ProjectId,
    int Order,
    Guid ActorId,
    string? IpAddress,
    string ActorRole = "") : IRequest<ServiceResult<EpicDto>>;

public class CreateEpicHandler : IRequestHandler<CreateEpicCommand, ServiceResult<EpicDto>>
{
    private readonly IEpicRepository _epics;
    private readonly IProjectRepository _projects;
    private readonly IAuditLogRepository _audit;
    private readonly IProjectAccessPolicy _access;

    public CreateEpicHandler(IEpicRepository epics, IProjectRepository projects, IAuditLogRepository audit, IProjectAccessPolicy access)
    {
        _epics = epics;
        _projects = projects;
        _audit = audit;
        _access = access;
    }

    public async Task<ServiceResult<EpicDto>> Handle(CreateEpicCommand cmd, CancellationToken ct)
    {
        var project = await _projects.GetByIdAsync(cmd.ProjectId, ct);
        if (project is null || project.Status == Domain.Projects.ProjectStatus.Archived)
            return ServiceResult<EpicDto>.Fail("NOT_FOUND", $"Project '{cmd.ProjectId}' not found or archived.");
        if (project.Status == Domain.Projects.ProjectStatus.Paused)
            return ServiceResult<EpicDto>.Fail("BUSINESS_RULE_VIOLATION", "This project is paused — cannot add epics to it.");

        if (!await _access.CanAccessProjectAsync(cmd.ProjectId, cmd.ActorId, cmd.ActorRole, ct))
            return ServiceResult<EpicDto>.Fail("FORBIDDEN", "You do not have access to this project.");

        var epic = Epic.Create(cmd.Title.Trim(), cmd.ProjectId, DescriptionSanitizer.Sanitize(cmd.Description?.Trim()), cmd.Order);
        if (cmd.AcceptanceCriteria is not null)
            epic.Update(epic.Title, epic.Description, DescriptionSanitizer.Sanitize(cmd.AcceptanceCriteria.Trim()), epic.Order);
        await _epics.AddAsync(epic, ct);
        await _epics.SaveChangesAsync(ct);

        await _audit.LogAsync("EPIC_CREATED", cmd.ActorId, cmd.IpAddress,
            $"Created epic {epic.Id} '{epic.Title}' in project {cmd.ProjectId}", ct);

        return ServiceResult<EpicDto>.Ok(EpicDto.From(epic));
    }
}
