using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Common;
using MediatR;

namespace Pulse.Application.Projects.Commands;

public record UpdateProjectCommand(
    Guid ProjectId,
    string? Name,
    string? Description,
    bool? Archive,
    Guid ActorId,
    string? IpAddress,
    string ActorRole = "",
    Guid? OwnerTeamId = null,
    bool ClearOwnerTeam = false,
    string? Code = null) : IRequest<ServiceResult<ProjectDto>>;

public class UpdateProjectHandler : IRequestHandler<UpdateProjectCommand, ServiceResult<ProjectDto>>
{
    private readonly IProjectRepository _projects;
    private readonly IAuditLogRepository _audit;
    private readonly IProjectAccessPolicy _access;

    public UpdateProjectHandler(IProjectRepository projects, IAuditLogRepository audit, IProjectAccessPolicy access)
    {
        _projects = projects;
        _audit = audit;
        _access = access;
    }

    public async Task<ServiceResult<ProjectDto>> Handle(UpdateProjectCommand cmd, CancellationToken ct)
    {
        var project = await _projects.GetByIdAsync(cmd.ProjectId, ct);
        if (project is null)
            return ServiceResult<ProjectDto>.Fail("NOT_FOUND", $"Project '{cmd.ProjectId}' not found.");

        if (!await _access.CanAccessProjectAsync(project.Id, cmd.ActorId, cmd.ActorRole, ct))
            return ServiceResult<ProjectDto>.Fail("FORBIDDEN", "You do not have access to this project.");

        if (cmd.Name is not null || cmd.Description is not null)
            project.Update(cmd.Name ?? project.Name, cmd.Description is not null ? DescriptionSanitizer.Sanitize(cmd.Description) : project.Description);

        if (cmd.Code is not null && !cmd.Code.Equals(project.Code, StringComparison.OrdinalIgnoreCase))
        {
            var normalizedCode = cmd.Code.Trim().ToUpperInvariant();
            var allCodes = await _projects.GetAllCodesAsync(ct);
            if (allCodes.Contains(normalizedCode))
                return ServiceResult<ProjectDto>.Fail("VALIDATION_ERROR", $"Project code '{normalizedCode}' is already in use.");

            try { project.SetCode(normalizedCode); }
            catch (DomainException ex) { return ServiceResult<ProjectDto>.Fail("VALIDATION_ERROR", ex.Message); }
        }

        if (cmd.OwnerTeamId.HasValue)
            project.SetOwnerTeam(cmd.OwnerTeamId.Value);
        else if (cmd.ClearOwnerTeam)
            project.SetOwnerTeam(null);

        if (cmd.Archive == true && project.Status == Domain.Projects.ProjectStatus.Active)
        {
            project.Archive();
            await _audit.LogAsync("PROJECT_ARCHIVED", cmd.ActorId, cmd.IpAddress,
                $"Archived project {project.Id} '{project.Name}'", ct);
        }
        else if (cmd.Name is not null || cmd.OwnerTeamId.HasValue || cmd.ClearOwnerTeam || cmd.Code is not null)
        {
            await _audit.LogAsync("PROJECT_UPDATED", cmd.ActorId, cmd.IpAddress,
                $"Updated project {project.Id} '{project.Name}'", ct);
        }

        await _projects.SaveChangesAsync(ct);

        return ServiceResult<ProjectDto>.Ok(ProjectDto.From(project));
    }
}
