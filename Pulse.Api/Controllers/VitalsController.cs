using System.Security.Claims;
using Asp.Versioning;
using Pulse.Api.Attributes;
using Pulse.Api.Authorization;
using Pulse.Application.Auth;
using Pulse.Api.Common;
using Pulse.Application.Common;
using Pulse.Application.Vitals;
using Pulse.Application.Vitals.Commands;
using Pulse.Application.Vitals.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Pulse.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/vitals")]
[Tags("Vitals")]
[EngineerAuth]
public class VitalsController : ControllerBase
{
    private readonly IMediator _mediator;

    public VitalsController(IMediator mediator) => _mediator = mediator;

    public record SubmitVitalsRequest(int Score, string? Comment);

    /// <summary>Submits the authenticated engineer's weekly vitals score (1–5).</summary>
    /// <remarks>One submission per engineer per week. Returns 409 if already submitted this week.</remarks>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<VitalsDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SubmitPulse([FromBody] SubmitVitalsRequest request)
    {
        var result = await _mediator.Send(new SubmitVitalsCommand(request.Score, request.Comment, GetActorId()));
        return result.ToCreatedResult();
    }

    /// <summary>Returns the authenticated engineer's own vitals history, newest week first.</summary>
    [HttpGet("me")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<VitalsDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetMyVitalsHistory() =>
        (await _mediator.Send(new GetMyVitalsHistoryQuery(GetActorId()))).ToActionResult();

    /// <summary>Returns all vitals responses. Any department head (their own department), PMO/PM/
    /// Head of Product (org-wide), or Executive/HR (org-wide reporting, same as their other
    /// queries).</summary>
    [HttpGet]
    [RequiresCapability(CapabilityRegistry.AnyHead, CapabilityRegistry.ExecutiveRead, CapabilityRegistry.HrRead)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<VitalsDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListPulse([FromQuery] DateOnly? weekOf) =>
        (await _mediator.Send(new ListVitalsQuery(weekOf, GetActorId(), GetRole()))).ToActionResult();

    /// <summary>Deletes a vitals response submitted by the caller.</summary>
    /// <remarks>
    /// Engineers may only delete their own vitals responses. The deletion is permanent.
    /// A SHA-256 hash of the deleted content is written to the audit log for accountability.
    /// </remarks>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeletePulse(Guid id) =>
        (await _mediator.Send(new DeleteVitalsCommand(id, GetActorId(), GetIp()))).ToNoContentResult();

    private Guid GetActorId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string GetRole() => User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;
    private string? GetIp() => HttpContext.Connection.RemoteIpAddress?.ToString();
}
