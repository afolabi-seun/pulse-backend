using System.Security.Claims;
using Asp.Versioning;
using Pulse.Api.Authorization;
using Pulse.Application.Auth;
using Pulse.Api.Common;
using Pulse.Application.Common;
using Pulse.Application.Thresholds;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Pulse.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/thresholds")]
[Tags("Thresholds")]
public class ThresholdsController : ControllerBase
{
    private readonly IMediator _mediator;

    public ThresholdsController(IMediator mediator) => _mediator = mediator;

    public record UpdateThresholdsRequest(
        double? LoadVsBaselineRatio,
        int? MaxConcurrentTasks,
        double? StaleCycleMultiplier,
        int? SignalsRequiredToFlag,
        double? EscalationT3Days,
        double? EscalationT3ElapsedPct,
        double? EscalationT1Days,
        double? EscalationT1ElapsedPct,
        double? EscalationT3MinHours,
        double? EscalationT1MinHours,
        int? QaLeadTimeDays,
        IReadOnlyList<PointScaleEntryDto>? PointScale,
        IReadOnlyList<PriorityScaleEntryDto>? PriorityScale);

    /// <summary>Returns the current overwork and escalation threshold values.</summary>
    [HttpGet]
    [RequiresCapability(CapabilityRegistry.AnyHead)]
    [ProducesResponseType(typeof(ApiResponse<ThresholdsDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetThresholds() =>
        (await _mediator.Send(new GetThresholdsQuery())).ToActionResult();

    /// <summary>Updates one or more threshold values. Only supplied fields are applied.</summary>
    /// <remarks>
    /// Restricted to the Head of PMO. Changes take effect immediately — the in-memory singleton and the
    /// background scanner both use the new values without a service restart.
    /// </remarks>
    [RequiresCapability(CapabilityRegistry.HeadOfPmoOnly)]
    [HttpPatch]
    [ProducesResponseType(typeof(ApiResponse<ThresholdsDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdateThresholds([FromBody] UpdateThresholdsRequest request) =>
        (await _mediator.Send(new UpdateThresholdsCommand(
            request.LoadVsBaselineRatio,
            request.MaxConcurrentTasks,
            request.StaleCycleMultiplier,
            request.SignalsRequiredToFlag,
            request.EscalationT3Days,
            request.EscalationT3ElapsedPct,
            request.EscalationT1Days,
            request.EscalationT1ElapsedPct,
            request.EscalationT3MinHours,
            request.EscalationT1MinHours,
            request.QaLeadTimeDays,
            request.PointScale,
            request.PriorityScale,
            GetActorId(), GetIp()))).ToActionResult();

    /// <summary>Returns every department's overwork threshold override. A field left null on a row
    /// means that field inherits the global default.</summary>
    [HttpGet("departments")]
    [RequiresCapability(CapabilityRegistry.AnyHead)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<DepartmentThresholdDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetDepartmentThresholds() =>
        (await _mediator.Send(new GetDepartmentThresholdsQuery())).ToActionResult();

    public record UpsertDepartmentThresholdRequest(
        double? LoadVsBaselineRatio,
        int? MaxConcurrentTasks,
        double? StaleCycleMultiplier,
        int? SignalsRequiredToFlag);

    /// <summary>Creates or replaces a department's threshold override.</summary>
    /// <remarks>Restricted to the Head of PMO, same as the global thresholds. A field omitted from
    /// the request inherits the global default — this replaces the whole override, it does not
    /// patch individual fields onto whatever was there before.</remarks>
    [HttpPut("departments/{department}")]
    [RequiresCapability(CapabilityRegistry.HeadOfPmoOnly)]
    [ProducesResponseType(typeof(ApiResponse<DepartmentThresholdDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpsertDepartmentThreshold(string department, [FromBody] UpsertDepartmentThresholdRequest request) =>
        (await _mediator.Send(new UpsertDepartmentThresholdCommand(
            department,
            request.LoadVsBaselineRatio,
            request.MaxConcurrentTasks,
            request.StaleCycleMultiplier,
            request.SignalsRequiredToFlag,
            GetActorId(), GetIp()))).ToActionResult();

    /// <summary>Removes a department's threshold override — it reverts to the global default.</summary>
    [HttpDelete("departments/{department}")]
    [RequiresCapability(CapabilityRegistry.HeadOfPmoOnly)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DeleteDepartmentThreshold(string department)
    {
        var result = await _mediator.Send(new DeleteDepartmentThresholdCommand(department, GetActorId(), GetIp()));
        return result.IsSuccess ? NoContent() : result.ToActionResult();
    }

    private Guid GetActorId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string? GetIp() => HttpContext.Connection.RemoteIpAddress?.ToString();
}
