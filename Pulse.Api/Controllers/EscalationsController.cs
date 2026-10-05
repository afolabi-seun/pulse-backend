using System.Security.Claims;
using Asp.Versioning;
using Pulse.Api.Authorization;
using Pulse.Application.Auth;
using Pulse.Api.Common;
using Pulse.Application.Common;
using Pulse.Application.Escalations;
using Pulse.Application.Escalations.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Pulse.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/escalations")]
[Tags("Escalations")]
[RequiresCapability(CapabilityRegistry.TeamLeadOrAbove, CapabilityRegistry.ExecutiveRead, CapabilityRegistry.HrRead)]
public class EscalationsController : ControllerBase
{
    private readonly IMediator _mediator;

    public EscalationsController(IMediator mediator) => _mediator = mediator;

    /// <summary>Returns all tasks currently at escalation level T-3, T-1, or Overdue.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<EscalationDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetEscalations() =>
        (await _mediator.Send(new GetEscalationsQuery(
            Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!),
            User.FindFirstValue(ClaimTypes.Role) ?? string.Empty))).ToActionResult();
}
