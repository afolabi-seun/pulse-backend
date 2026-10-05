using System.Security.Claims;
using Asp.Versioning;
using Pulse.Api.Attributes;
using Pulse.Api.Authorization;
using Pulse.Application.Auth;
using Pulse.Api.Common;
using Pulse.Application.Common;
using Pulse.Application.Retrospectives;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Pulse.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/sprints/{sprintId:guid}/retrospective")]
[Tags("Retrospective")]
[EngineerAuth]
public class RetrospectiveController : ControllerBase
{
    private readonly IMediator _mediator;

    public RetrospectiveController(IMediator mediator) => _mediator = mediator;

    public record UpsertRetroRequest(string WentWell, string NeedsImprovement, string ActionItems);

    /// <summary>Returns the retrospective for a sprint, or null if not yet created.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<RetroDto?>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid sprintId, CancellationToken ct) =>
        (await _mediator.Send(new GetRetroQuery(sprintId,
            Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!),
            User.FindFirstValue(ClaimTypes.Role) ?? string.Empty), ct)).ToActionResult();

    /// <summary>Creates or updates the retrospective for a sprint. Requires team lead or above.</summary>
    [HttpPut]
    [RequiresCapability(CapabilityRegistry.TeamLeadOrAbove)]
    [ProducesResponseType(typeof(ApiResponse<RetroDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<RetroDto>), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Upsert(Guid sprintId, [FromBody] UpsertRetroRequest request, CancellationToken ct) =>
        (await _mediator.Send(new UpsertRetroCommand(
            sprintId,
            Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!),
            request.WentWell,
            request.NeedsImprovement,
            request.ActionItems), ct)).ToActionResult();
}
