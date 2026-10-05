using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Common;
using Pulse.Domain.Projects;
using MediatR;

namespace Pulse.Application.Projects.Commands;

public record CreateProjectCommand(
    string Name,
    string? Description,
    Guid ActorId,
    string? IpAddress,
    Guid? OwnerTeamId = null,
    string? Code = null) : IRequest<ServiceResult<ProjectDto>>;

public class CreateProjectHandler : IRequestHandler<CreateProjectCommand, ServiceResult<ProjectDto>>
{
    private readonly IProjectRepository  _projects;
    private readonly IAuditLogRepository _audit;

    public CreateProjectHandler(IProjectRepository projects, IAuditLogRepository audit)
    {
        _projects = projects;
        _audit    = audit;
    }

    public async Task<ServiceResult<ProjectDto>> Handle(CreateProjectCommand cmd, CancellationToken ct)
    {
        var allCodes = await _projects.GetAllCodesAsync(ct);

        string code;
        if (!string.IsNullOrWhiteSpace(cmd.Code))
        {
            code = cmd.Code.Trim().ToUpperInvariant();
            if (allCodes.Contains(code))
                return ServiceResult<ProjectDto>.Fail("VALIDATION_ERROR", $"Project code '{code}' is already in use.");
        }
        else
        {
            code = ProjectCodeGenerator.MakeUnique(ProjectCodeGenerator.DeriveBase(cmd.Name), new HashSet<string>(allCodes));
        }

        Project project;
        try { project = Project.Create(cmd.Name, DescriptionSanitizer.Sanitize(cmd.Description), cmd.OwnerTeamId, code); }
        catch (DomainException ex) { return ServiceResult<ProjectDto>.Fail("VALIDATION_ERROR", ex.Message); }

        await _projects.AddAsync(project, ct);
        await _projects.SaveChangesAsync(ct);

        await _audit.LogAsync("PROJECT_CREATED", cmd.ActorId, cmd.IpAddress,
            $"Created project {project.Id} '{project.Name}'", ct);

        return ServiceResult<ProjectDto>.Ok(ProjectDto.From(project));
    }
}
