using System.Security.Claims;
using Asp.Versioning;
using Pulse.Api.Attributes;
using Pulse.Api.Authorization;
using Pulse.Api.Common;
using Pulse.Application.Alerts;
using Pulse.Application.Alerts.Commands;
using Pulse.Application.Alerts.Queries;
using Pulse.Application.Auth;
using Pulse.Application.Common;
using Pulse.Domain.Alerts;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Pulse.Api.Controllers;

/// <summary>User-configured watches on a metric Pulse already computes (blocker count, team
/// velocity, check-in compliance, QA reject rate) — evaluated on a schedule by AlertRuleScanner.
/// Gated to Team Lead and above: this is a management-facing feature, not a per-engineer one.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/alert-rules")]
[Tags("Alert Rules")]
[EngineerAuth]
[RequiresCapability(CapabilityRegistry.TeamLeadOrAbove)]
public class AlertRulesController : ControllerBase
{
    private readonly IMediator _mediator;

    public AlertRulesController(IMediator mediator) => _mediator = mediator;

    public record CreateAlertRuleRequest(
        string Name, string Metric, string ScopeType, Guid ScopeId,
        string Comparator, double Threshold, bool DeliverInApp, bool DeliverEmail,
        bool DeliverWebhook = false, string? WebhookUrl = null, string? SlackChannel = null, string? GoogleChatSpaceId = null);

    public record UpdateAlertRuleRequest(
        string Name, string Comparator, double Threshold,
        bool DeliverInApp, bool DeliverEmail, bool? IsActive,
        bool DeliverWebhook = false, string? WebhookUrl = null, string? SlackChannel = null, string? GoogleChatSpaceId = null);

    /// <summary>Lists the caller's own alert rules.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<AlertRuleDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListMine() =>
        (await _mediator.Send(new ListMyAlertRulesQuery(GetActorId()))).ToActionResult();

    /// <summary>Every Google Chat space the app has been added to — feeds the Google Chat space
    /// picker on the My Alerts page (see ListGoogleChatSpacesQuery for why this can't be a
    /// free-text field the way Slack's channel is).</summary>
    [HttpGet("google-chat-spaces")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<GoogleChatSpaceOptionDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListGoogleChatSpaces() =>
        (await _mediator.Send(new ListGoogleChatSpacesQuery())).ToActionResult();

    /// <summary>Creates a new alert rule watching a metric on a team or project.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<AlertRuleDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Create([FromBody] CreateAlertRuleRequest request)
    {
        if (!Enum.TryParse<AlertMetric>(request.Metric, ignoreCase: true, out var metric))
            return ServiceResult<AlertRuleDto>.Fail("BUSINESS_RULE_VIOLATION", $"Unknown metric '{request.Metric}'.").ToActionResult();
        if (!Enum.TryParse<AlertScopeType>(request.ScopeType, ignoreCase: true, out var scopeType))
            return ServiceResult<AlertRuleDto>.Fail("BUSINESS_RULE_VIOLATION", $"Unknown scope type '{request.ScopeType}'.").ToActionResult();
        if (!Enum.TryParse<AlertComparator>(request.Comparator, ignoreCase: true, out var comparator))
            return ServiceResult<AlertRuleDto>.Fail("BUSINESS_RULE_VIOLATION", $"Unknown comparator '{request.Comparator}'.").ToActionResult();

        var result = await _mediator.Send(new CreateAlertRuleCommand(
            request.Name, metric, scopeType, request.ScopeId, comparator, request.Threshold,
            request.DeliverInApp, request.DeliverEmail, GetActorId(), GetRole(),
            request.DeliverWebhook, request.WebhookUrl, request.SlackChannel, request.GoogleChatSpaceId));

        return result.ToCreatedResult();
    }

    /// <summary>Updates an existing alert rule's name/threshold/comparator/delivery/active state.</summary>
    [HttpPatch("{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse<AlertRuleDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateAlertRuleRequest request)
    {
        if (!Enum.TryParse<AlertComparator>(request.Comparator, ignoreCase: true, out var comparator))
            return ServiceResult<AlertRuleDto>.Fail("BUSINESS_RULE_VIOLATION", $"Unknown comparator '{request.Comparator}'.").ToActionResult();

        return (await _mediator.Send(new UpdateAlertRuleCommand(
            id, request.Name, comparator, request.Threshold, request.DeliverInApp, request.DeliverEmail,
            request.IsActive, GetActorId(), request.DeliverWebhook, request.WebhookUrl, request.SlackChannel, request.GoogleChatSpaceId))).ToActionResult();
    }

    /// <summary>Deletes an alert rule.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Delete(Guid id)
    {
        var result = await _mediator.Send(new DeleteAlertRuleCommand(id, GetActorId()));
        return result.IsSuccess ? NoContent() : result.ToActionResult();
    }

    private Guid GetActorId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string GetRole() => User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;
}
