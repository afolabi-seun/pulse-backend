using System.Security.Claims;
using Asp.Versioning;
using Pulse.Api.Attributes;
using Pulse.Api.Authorization;
using Pulse.Api.Common;
using Pulse.Application.Auth;
using Pulse.Application.Common;
using Pulse.Application.Integrations.GoogleChat;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Pulse.Api.Controllers;

/// <summary>
/// Linking Google Chat spaces to the caller's organization (multi-tenancy Phase 2c). A head issues a one-time
/// code here and types "@Pulse link CODE" in a space Pulse has been added to; the Chat events endpoint does
/// the linking.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/integrations/google-chat")]
[Tags("Google Chat")]
[EngineerAuth]
[RequiresCapability(CapabilityRegistry.AnyHead)]
public class GoogleChatIntegrationController(IMediator mediator) : ControllerBase
{
    /// <summary>Whether Google Chat is set up on this server, and the spaces linked to the caller's organization.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<GoogleChatConnectionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetConnection(CancellationToken ct) =>
        (await mediator.Send(new GetGoogleChatConnectionQuery(), ct)).ToActionResult();

    /// <summary>A one-time code (valid 15 minutes) to type in a space as "@Pulse link CODE".</summary>
    [HttpPost("link-codes")]
    [ProducesResponseType(typeof(ApiResponse<GoogleChatLinkCodeDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> CreateLinkCode(CancellationToken ct) =>
        (await mediator.Send(new CreateGoogleChatLinkCodeCommand(ActorId), ct)).ToActionResult();

    /// <summary>Unlinks one of the organization's spaces.</summary>
    [HttpDelete("spaces/{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse<bool>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Unlink(Guid id, CancellationToken ct) =>
        (await mediator.Send(new UnlinkGoogleChatSpaceCommand(id, ActorId, HttpContext.Connection.RemoteIpAddress?.ToString()), ct)).ToActionResult();

    private Guid ActorId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
