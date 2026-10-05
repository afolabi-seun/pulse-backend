using System.Security.Claims;
using Asp.Versioning;
using Pulse.Api.Attributes;
using Pulse.Api.Authorization;
using Pulse.Application.Auth;
using Pulse.Api.Common;
using Pulse.Application.Common;
using Pulse.Application.Projects;
using Pulse.Application.Projects.Commands;
using Pulse.Application.Projects.Queries;
using Pulse.Domain.Engineers;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;

namespace Pulse.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/projects")]
[Tags("Projects")]
public class ProjectsController : ControllerBase
{
    private readonly IMediator _mediator;

    public ProjectsController(IMediator mediator) => _mediator = mediator;

    public record CreateProjectRequest(string Name, string? Description, Guid? OwnerTeamId = null, string? Code = null);
    public record UpdateProjectRequest(string? Name, string? Description, bool? Archive, Guid? OwnerTeamId = null, bool ClearOwnerTeam = false, string? Code = null);

    /// <summary>Returns projects the caller has assigned tasks in. Available to all authenticated users.</summary>
    [HttpGet("mine")]
    [EngineerAuth]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<MyProjectDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetMyProjects() =>
        (await _mediator.Send(new ListMyProjectsQuery(GetActorId()))).ToActionResult();

    /// <summary>Returns all active projects.</summary>
    /// <remarks>Every row includes a per-caller canAccess flag rather than filtering the list down —
    /// team leads and department heads already need this shape to compute it (see
    /// ListProjectsHandler); team leads get it here too so they can filter their own tasks by
    /// project without also being handed the full Projects management page.</remarks>
    [HttpGet]
    [RequiresCapability(CapabilityRegistry.TeamLeadOrAbove, CapabilityRegistry.ExecutiveRead, CapabilityRegistry.HrRead, CapabilityRegistry.AccountantRead)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<ProjectDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListProjects() =>
        (await _mediator.Send(new ListProjectsQuery(GetActorId(), GetRole()))).ToActionResult();

    /// <summary>Creates a new project. PMO, Head of Product, or Head of Functional.</summary>
    [HttpPost]
    [RequiresCapability(CapabilityRegistry.ProjectCreatorOrAbove)]
    [ProducesResponseType(typeof(ApiResponse<ProjectDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateProject([FromBody] CreateProjectRequest request)
    {
        var result = await _mediator.Send(new CreateProjectCommand(
            request.Name, request.Description, GetActorId(), GetIp(), request.OwnerTeamId, request.Code));

        return result.ToCreatedResult("GetProject", new { id = result.Data?.Id });
    }

    /// <summary>Returns a single project by ID.</summary>
    [HttpGet("{id:guid}", Name = "GetProject")]
    [EngineerAuth]
    [ProducesResponseType(typeof(ApiResponse<ProjectDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetProject(Guid id) =>
        (await _mediator.Send(new GetProjectQuery(id, GetActorId(), GetRole()))).ToActionResult();

    /// <summary>Updates a project's name, description, or owner team (any PM+). Archiving requires PMO.</summary>
    [HttpPatch("{id:guid}")]
    [RequiresCapability(CapabilityRegistry.PmOrAbove)]
    [ProducesResponseType(typeof(ApiResponse<ProjectDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdateProject(Guid id, [FromBody] UpdateProjectRequest request)
    {
        if (request.Archive == true)
        {
            var role = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value;
            if (role is not Roles.HeadOfPmo and not Roles.ProjectManager)
                return StatusCode(StatusCodes.Status403Forbidden,
                    ApiResponse<object>.Failure("FORBIDDEN", "Only project managers and PMO can archive projects."));
        }

        return (await _mediator.Send(new UpdateProjectCommand(
            id, request.Name, request.Description, request.Archive,
            GetActorId(), GetIp(), GetRole(), request.OwnerTeamId, request.ClearOwnerTeam, request.Code))).ToActionResult();
    }

    /// <summary>Puts a project on hold — pauses every active or blocked task in it (they stop counting
    /// toward workload/escalations) and stops new tasks/epics being added until it's resumed.</summary>
    [HttpPost("{id:guid}/pause")]
    [RequiresCapability(CapabilityRegistry.PmOrAbove)]
    [ProducesResponseType(typeof(ApiResponse<ProjectDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> PauseProject(Guid id) =>
        (await _mediator.Send(new PauseProjectCommand(id, GetActorId(), GetIp(), GetRole()))).ToActionResult();

    /// <summary>Resumes a paused project — reactivates the tasks it paused (voluntarily-paused tasks are left
    /// alone) and shifts their due dates forward by however long the project was on hold.</summary>
    [HttpDelete("{id:guid}/pause")]
    [RequiresCapability(CapabilityRegistry.PmOrAbove)]
    [ProducesResponseType(typeof(ApiResponse<ProjectDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ResumeProject(Guid id) =>
        (await _mediator.Send(new ResumeProjectCommand(id, GetActorId(), GetIp(), GetRole()))).ToActionResult();

    /// <summary>Returns weekly delivered story points for the project over the 6-week rolling window.</summary>
    [HttpGet("{id:guid}/throughput")]
    [EngineerAuth]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<ThroughputWeekDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetProjectThroughput(Guid id) =>
        (await _mediator.Send(new GetProjectThroughputQuery(id, GetActorId(), GetRole()))).ToActionResult();

    /// <summary>Returns a page of the project's recent activity feed — task-history events across every
    /// task in it, newest first.</summary>
    [HttpGet("{id:guid}/activity")]
    [EngineerAuth]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<ProjectActivityDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetProjectActivity(Guid id, [FromQuery] int limit = 50, [FromQuery] string? cursor = null) =>
        (await _mediator.Send(new GetProjectActivityQuery(id, GetActorId(), GetRole(), limit, cursor))).ToActionResult();

    /// <summary>Permanently deletes a project. Rejected if the project has any tasks. PMO or Head of Product.</summary>
    [HttpDelete("{id:guid}")]
    [RequiresCapability(CapabilityRegistry.ProjectDeleterOrAbove)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DeleteProject(Guid id)
    {
        var result = await _mediator.Send(new DeleteProjectCommand(id, GetActorId(), GetIp(), GetRole()));
        return result.IsSuccess ? NoContent() : result.ToActionResult();
    }

    // ── Project Members ──────────────────────────────────────────────────────

    public record AddMemberRequest(Guid EngineerId);

    /// <summary>Lists all engineers who are members of this project.</summary>
    [HttpGet("{id:guid}/members")]
    [EngineerAuth]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<ProjectMemberDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListMembers(Guid id) =>
        (await _mediator.Send(new ListProjectMembersQuery(id, GetActorId(), GetRole()))).ToActionResult();

    /// <summary>Adds an engineer as a member of this project. PM+ only.</summary>
    [HttpPost("{id:guid}/members")]
    [RequiresCapability(CapabilityRegistry.PmOrAbove)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<ProjectMemberDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddMember(Guid id, [FromBody] AddMemberRequest request) =>
        (await _mediator.Send(new AddProjectMemberCommand(id, request.EngineerId, GetActorId(), GetRole()))).ToActionResult();

    public record AddTeamMembersRequest(Guid TeamId);

    /// <summary>Adds every active member of a team to this project in one step. PM+ only.</summary>
    [HttpPost("{id:guid}/members/team")]
    [RequiresCapability(CapabilityRegistry.PmOrAbove)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<ProjectMemberDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddTeamMembers(Guid id, [FromBody] AddTeamMembersRequest request) =>
        (await _mediator.Send(new AddProjectTeamMembersCommand(id, request.TeamId, GetActorId(), GetRole()))).ToActionResult();

    /// <summary>Removes an engineer from this project's member list. PM+ only.</summary>
    [HttpDelete("{id:guid}/members/{engineerId:guid}")]
    [RequiresCapability(CapabilityRegistry.PmOrAbove)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<ProjectMemberDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveMember(Guid id, Guid engineerId) =>
        (await _mediator.Send(new RemoveProjectMemberCommand(id, engineerId, GetActorId(), GetRole()))).ToActionResult();

    // ── Project Following (department heads and team leads) ──────────────────

    /// <summary>Returns all projects the caller is currently following, with live task summaries.</summary>
    [HttpGet("followed")]
    [RequiresCapability(CapabilityRegistry.ProjectFollow)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<FollowedProjectDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetFollowedProjects() =>
        (await _mediator.Send(new GetFollowedProjectsQuery(GetActorId(), GetRole()))).ToActionResult();

    /// <summary>Follows a project. Notifies the project's team leads. Idempotent.</summary>
    [HttpPost("{id:guid}/follow")]
    [RequiresCapability(CapabilityRegistry.ProjectFollow)]
    [ProducesResponseType(typeof(ApiResponse<bool>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> FollowProject(Guid id) =>
        (await _mediator.Send(new FollowProjectCommand(id, GetActorId(), GetRole()))).ToActionResult();

    /// <summary>Unfollows a project. Idempotent.</summary>
    [HttpDelete("{id:guid}/follow")]
    [RequiresCapability(CapabilityRegistry.ProjectFollow)]
    [ProducesResponseType(typeof(ApiResponse<bool>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UnfollowProject(Guid id) =>
        (await _mediator.Send(new UnfollowProjectCommand(id, GetActorId()))).ToActionResult();

    private Guid GetActorId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string GetRole() => User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;
    private string? GetIp() => HttpContext.Connection.RemoteIpAddress?.ToString();
}
