using System.Security.Claims;
using Asp.Versioning;
using Pulse.Api.Authorization;
using Pulse.Application.Auth;
using Pulse.Api.Common;
using Pulse.Application.Common;
using Pulse.Application.Projects.Queries;
using Pulse.Application.Teams;
using Pulse.Application.Teams.Commands;
using Pulse.Application.Teams.Queries;
using Pulse.Domain.Engineers;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Pulse.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/teams")]
[Tags("Teams")]
public class TeamsController : ControllerBase
{
    private readonly IMediator _mediator;

    public TeamsController(IMediator mediator) => _mediator = mediator;

    public record CreateTeamRequest(string Name, Guid? TeamLeadId, string? Department);
    public record UpdateTeamRequest(string? Name, Guid? TeamLeadId, bool? ClearTeamLead, bool? Deactivate, string? Department, bool? ClearDepartment);

    /// <summary>Returns all teams.</summary>
    [HttpGet]
    [RequiresCapability(CapabilityRegistry.PmOrAbove, CapabilityRegistry.ExecutiveRead, CapabilityRegistry.HrRead, CapabilityRegistry.AccountantRead)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<TeamDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListTeams() =>
        (await _mediator.Send(new ListTeamsQuery(GetCallerRole(), GetActorId()))).ToActionResult();

    /// <summary>Creates a new team. PMO, or Product's manager/head.</summary>
    [HttpPost]
    [RequiresCapability(CapabilityRegistry.TeamCreatorOrAbove)]
    [ProducesResponseType(typeof(ApiResponse<TeamDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateTeam([FromBody] CreateTeamRequest request)
    {
        var result = await _mediator.Send(new CreateTeamCommand(
            request.Name, request.TeamLeadId, request.Department, GetActorId(), GetIp()));

        return result.ToCreatedResult("GetTeam", new { id = result.Data?.Id });
    }

    /// <summary>Returns a single team by ID.</summary>
    [HttpGet("{id:guid}", Name = "GetTeam")]
    [RequiresCapability(CapabilityRegistry.PmOrAbove)]
    [ProducesResponseType(typeof(ApiResponse<TeamDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetTeam(Guid id) =>
        (await _mediator.Send(new GetTeamQuery(id))).ToActionResult();

    /// <summary>Updates a team's name, team lead, department, or deactivates it. PMO only.</summary>
    [HttpPatch("{id:guid}")]
    [RequiresCapability(CapabilityRegistry.PmoOnly)]
    [ProducesResponseType(typeof(ApiResponse<TeamDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdateTeam(Guid id, [FromBody] UpdateTeamRequest request) =>
        (await _mediator.Send(new UpdateTeamCommand(
            id, request.Name, request.TeamLeadId, request.ClearTeamLead,
            request.Deactivate, request.Department, request.ClearDepartment,
            GetActorId(), GetIp()))).ToActionResult();

    /// <summary>Returns delivered story points per week over the 6-week rolling window, summed across
    /// the team's engineers — the team-level analogue of the per-engineer/per-project throughput charts.</summary>
    [HttpGet("{id:guid}/throughput")]
    [RequiresCapability(CapabilityRegistry.TeamLeadOrAbove, CapabilityRegistry.ExecutiveRead, CapabilityRegistry.HrRead)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<ThroughputWeekDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetTeamThroughput(Guid id) =>
        (await _mediator.Send(new GetTeamThroughputQuery(id, GetActorId(), GetCallerRole()))).ToActionResult();

    private Guid GetActorId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string GetCallerRole() => User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;
    private string? GetIp() => HttpContext.Connection.RemoteIpAddress?.ToString();
}
