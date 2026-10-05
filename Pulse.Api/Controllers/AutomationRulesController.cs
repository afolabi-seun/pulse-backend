using System.Security.Claims;
using Asp.Versioning;
using Pulse.Api.Attributes;
using Pulse.Api.Authorization;
using Pulse.Api.Common;
using Pulse.Application.Automations;
using Pulse.Application.Automations.Commands;
using Pulse.Application.Automations.Queries;
using Pulse.Application.Auth;
using Pulse.Application.Common;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Pulse.Api.Controllers;

/// <summary>User-configured automations that take a real action — currently just one: reassign a
/// task that's sat Blocked past a threshold to its team's lead. Evaluated on a schedule by
/// AutomationRuleScanner. Deliberately narrower in scope than AlertRules' notify-only model — see
/// AutomationRule's own doc comment. Gated to Team Lead and above, same as AlertRulesController.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/automation-rules")]
[Tags("Automation Rules")]
[EngineerAuth]
[RequiresCapability(CapabilityRegistry.TeamLeadOrAbove)]
public class AutomationRulesController : ControllerBase
{
    private readonly IMediator _mediator;

    public AutomationRulesController(IMediator mediator) => _mediator = mediator;

    public record CreateAutomationRuleRequest(string Name, Guid TeamId, int ThresholdDays);
    public record UpdateAutomationRuleRequest(string Name, int ThresholdDays, bool? IsActive);

    /// <summary>Lists the caller's own automation rules.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<AutomationRuleDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListMine() =>
        (await _mediator.Send(new ListMyAutomationRulesQuery(GetActorId()))).ToActionResult();

    /// <summary>Creates a new automation rule for a team.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<AutomationRuleDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Create([FromBody] CreateAutomationRuleRequest request)
    {
        var result = await _mediator.Send(new CreateAutomationRuleCommand(
            request.Name, request.TeamId, request.ThresholdDays, GetActorId(), GetRole()));

        return result.ToCreatedResult();
    }

    /// <summary>Updates an existing automation rule's name/threshold/active state.</summary>
    [HttpPatch("{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse<AutomationRuleDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateAutomationRuleRequest request) =>
        (await _mediator.Send(new UpdateAutomationRuleCommand(
            id, request.Name, request.ThresholdDays, request.IsActive, GetActorId()))).ToActionResult();

    /// <summary>Deletes an automation rule.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Delete(Guid id)
    {
        var result = await _mediator.Send(new DeleteAutomationRuleCommand(id, GetActorId()));
        return result.IsSuccess ? NoContent() : result.ToActionResult();
    }

    private Guid GetActorId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string GetRole() => User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;
}
