using System.Security.Claims;
using Asp.Versioning;
using Pulse.Api.Attributes;
using Pulse.Api.Authorization;
using Pulse.Api.Common;
using Pulse.Application.Auth;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Integrations.Slack;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Pulse.Api.Controllers;

/// <summary>
/// Connecting an organization's own Slack workspace (multi-tenancy Phase 2b). A head gets an install URL,
/// approves Pulse in Slack, and Slack redirects the browser to <see cref="OAuthCallback"/>, which stores the
/// workspace's bot token for the org named in the signed state and sends the browser back to the app.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/integrations/slack")]
[Tags("Slack")]
public class SlackIntegrationController(IMediator mediator, IAppSettings settings) : ControllerBase
{
    /// <summary>Whether Slack is available on this server and connected for the caller's organization.</summary>
    [HttpGet]
    [EngineerAuth]
    [RequiresCapability(CapabilityRegistry.AnyHead)]
    [ProducesResponseType(typeof(ApiResponse<SlackConnectionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetConnection(CancellationToken ct) =>
        (await mediator.Send(new GetSlackConnectionQuery(), ct)).ToActionResult();

    /// <summary>The "Add to Slack" URL to send the browser to. Valid for 10 minutes.</summary>
    [HttpGet("install-url")]
    [EngineerAuth]
    [RequiresCapability(CapabilityRegistry.AnyHead)]
    [ProducesResponseType(typeof(ApiResponse<string>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetInstallUrl(CancellationToken ct) =>
        (await mediator.Send(new GetSlackInstallUrlQuery(ActorId), ct)).ToActionResult();

    /// <summary>Slack's OAuth redirect. Anonymous — the signed state identifies the organization and
    /// installer. Always redirects back to the app with the outcome.</summary>
    [HttpGet("oauth/callback")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<IActionResult> OAuthCallback(
        [FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error, CancellationToken ct)
    {
        var result = await mediator.Send(new CompleteSlackInstallCommand(code, state, error,
            HttpContext.Connection.RemoteIpAddress?.ToString()), ct);
        var query = result.IsSuccess
            ? "slack=connected"
            : $"slack=error&reason={Uri.EscapeDataString(result.ErrorCode ?? "unknown")}";
        return Redirect($"{settings.AppBaseUrl}/admin/integrations?{query}");
    }

    /// <summary>Disconnects the caller's organization from its Slack workspace.</summary>
    [HttpDelete]
    [EngineerAuth]
    [RequiresCapability(CapabilityRegistry.AnyHead)]
    [ProducesResponseType(typeof(ApiResponse<bool>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Disconnect(CancellationToken ct) =>
        (await mediator.Send(new DisconnectSlackCommand(ActorId, HttpContext.Connection.RemoteIpAddress?.ToString()), ct)).ToActionResult();

    private Guid ActorId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
