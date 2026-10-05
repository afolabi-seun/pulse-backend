using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Common;
using Pulse.Domain.Sprints;
using MediatR;

namespace Pulse.Application.Sprints.Commands;

public record CreateSprintCommand(
    Guid ProjectId,
    string Name,
    string? Goal,
    DateOnly StartDate,
    DateOnly EndDate,
    Guid ActorId,
    string? IpAddress) : IRequest<ServiceResult<SprintDto>>;

public class CreateSprintHandler : IRequestHandler<CreateSprintCommand, ServiceResult<SprintDto>>
{
    private readonly ISprintRepository _sprints;
    private readonly IProjectRepository _projects;
    private readonly IAuditLogRepository _audit;

    public CreateSprintHandler(ISprintRepository sprints, IProjectRepository projects, IAuditLogRepository audit)
    {
        _sprints = sprints;
        _projects = projects;
        _audit = audit;
    }

    public async Task<ServiceResult<SprintDto>> Handle(CreateSprintCommand cmd, CancellationToken ct)
    {
        var project = await _projects.GetByIdAsync(cmd.ProjectId, ct);
        if (project is null)
            return ServiceResult<SprintDto>.Fail("NOT_FOUND", $"Project '{cmd.ProjectId}' not found.");
        if (project.OwnerTeamId is null)
            return ServiceResult<SprintDto>.Fail("BUSINESS_RULE_VIOLATION", "This project has no owning team, so a sprint cannot be created for it.");

        Sprint sprint;
        try
        {
            sprint = Sprint.Create(project.OwnerTeamId.Value, cmd.ProjectId, cmd.Name, cmd.StartDate, cmd.EndDate,
                cmd.Goal is not null ? DescriptionSanitizer.Sanitize(cmd.Goal) : null);
        }
        catch (DomainException ex)
        {
            return ServiceResult<SprintDto>.Fail("BUSINESS_RULE_VIOLATION", ex.Message);
        }

        await _sprints.AddAsync(sprint, ct);
        await _sprints.SaveChangesAsync(ct);

        await _audit.LogAsync("SPRINT_CREATED", cmd.ActorId, cmd.IpAddress,
            $"Created sprint {sprint.Id} '{sprint.Name}' for team {sprint.TeamId}", ct);

        return ServiceResult<SprintDto>.Ok(SprintDto.From(sprint, project.Name));
    }
}
