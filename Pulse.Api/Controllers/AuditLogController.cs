using System.Security.Claims;
using Asp.Versioning;
using Pulse.Api.Attributes;
using Pulse.Api.Authorization;
using Pulse.Application.Auth;
using Pulse.Api.Common;
using Pulse.Application.AuditLog;
using Pulse.Application.Common;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Pulse.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/audit-log")]
[Tags("AuditLog")]
public class AuditLogController : ControllerBase
{
    private readonly IMediator _mediator;

    public AuditLogController(IMediator mediator) => _mediator = mediator;

    /// <summary>Returns a paginated audit log, most recent first. Requires any department head role.</summary>
    [HttpGet]
    [RequiresCapability(CapabilityRegistry.HeadOnly)]
    [ProducesResponseType(typeof(ApiResponse<AuditLogPageDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListAuditLog(
        [FromQuery] long? cursor,
        [FromQuery] int limit = 50,
        [FromQuery] Guid? actorId = null,
        [FromQuery] string? action = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null) =>
        (await _mediator.Send(new ListAuditLogQuery(cursor, limit, actorId, action, from, to))).ToActionResult();

    /// <summary>Returns the current user's own audit trail, most recent first. Available to all authenticated users.</summary>
    [HttpGet("me")]
    [EngineerAuth]
    [ProducesResponseType(typeof(ApiResponse<AuditLogPageDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetMyAuditLog(
        [FromQuery] long? cursor,
        [FromQuery] int limit = 25)
    {
        var callerId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        return (await _mediator.Send(new ListAuditLogQuery(cursor, limit, callerId, null, null, null))).ToActionResult();
    }
}
