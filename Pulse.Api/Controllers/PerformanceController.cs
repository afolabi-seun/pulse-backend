using System.Security.Claims;
using Asp.Versioning;
using Pulse.Api.Attributes;
using Pulse.Api.Authorization;
using Pulse.Application.Auth;
using Pulse.Api.Common;
using Pulse.Application.Common;
using Pulse.Application.Performance;
using Pulse.Application.Performance.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Pulse.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/performance")]
[Tags("Performance")]
[EngineerAuth]
public class PerformanceController : ControllerBase
{
    private readonly IMediator _mediator;

    public PerformanceController(IMediator mediator) => _mediator = mediator;

    private Guid GetActorId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string GetRole() => User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;

    /// <summary>Returns the authenticated engineer's own performance metrics over a rolling window.</summary>
    [HttpGet("me")]
    [ProducesResponseType(typeof(ApiResponse<PerformanceMetricsDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetMyPerformance([FromQuery] int days = 30) =>
        (await _mediator.Send(new GetMyPerformanceQuery(GetActorId(), days))).ToActionResult();

    /// <summary>Returns per-engineer performance for a team. Optionally scope to a single project via ?projectId=.</summary>
    [HttpGet("team/{teamId:guid}")]
    [RequiresCapability(CapabilityRegistry.TeamLeadOrAbove, CapabilityRegistry.ExecutiveRead, CapabilityRegistry.HrRead)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<PerformanceMetricsDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetTeamPerformance(Guid teamId, [FromQuery] Guid? projectId, [FromQuery] int days = 30) =>
        (await _mediator.Send(new GetTeamPerformanceQuery(teamId, projectId, GetActorId(), GetRole(), days))).ToActionResult();

    /// <summary>Returns project-level aggregate performance across its members.</summary>
    [HttpGet("project/{projectId:guid}")]
    [RequiresCapability(CapabilityRegistry.PmOrAbove, CapabilityRegistry.ExecutiveRead, CapabilityRegistry.HrRead)]
    [ProducesResponseType(typeof(ApiResponse<ProjectPerformanceDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetProjectPerformance(Guid projectId, [FromQuery] int days = 30) =>
        (await _mediator.Send(new GetProjectPerformanceQuery(projectId, GetActorId(), GetRole(), days))).ToActionResult();
}
