using System.Security.Claims;
using Asp.Versioning;
using Pulse.Api.Attributes;
using Pulse.Api.Authorization;
using Pulse.Application.Auth;
using Pulse.Api.Common;
using Pulse.Application.Common;
using Pulse.Application.Epics;
using Pulse.Application.Epics.Commands;
using Pulse.Application.Epics.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Pulse.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/epics")]
[Tags("Epics")]
[EngineerAuth]
public class EpicsController : ControllerBase
{
    private readonly IMediator _mediator;

    public EpicsController(IMediator mediator) => _mediator = mediator;

    public record CreateEpicRequest(string Title, string? Description, string? AcceptanceCriteria, Guid ProjectId, int Order = 0);
    public record UpdateEpicRequest(string? Title, string? Description, string? AcceptanceCriteria, string? Status, int? Order, Guid? SprintId, bool? RemoveFromSprint);

    /// <summary>Returns all epics for a project. Pass backlogOnly=true to return only epics not in any sprint.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<EpicDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListEpics([FromQuery] Guid projectId, [FromQuery] bool backlogOnly = false) =>
        (await _mediator.Send(new ListEpicsQuery(projectId, GetActorId(), GetRole(), backlogOnly))).ToActionResult();

    /// <summary>Creates a new epic in a project.</summary>
    [HttpPost]
    [RequiresCapability(CapabilityRegistry.ProductManagerOrAbove)]
    [ProducesResponseType(typeof(ApiResponse<EpicDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateEpic([FromBody] CreateEpicRequest request)
    {
        var result = await _mediator.Send(new CreateEpicCommand(
            request.Title, request.Description, request.AcceptanceCriteria, request.ProjectId, request.Order,
            GetActorId(), GetIp(), GetRole()));
        return result.ToCreatedResult("GetEpic", new { id = result.Data?.Id });
    }

    /// <summary>Returns a single epic by ID.</summary>
    [HttpGet("{id:guid}", Name = "GetEpic")]
    [ProducesResponseType(typeof(ApiResponse<EpicDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetEpic(Guid id) =>
        (await _mediator.Send(new GetEpicQuery(id, GetActorId(), GetRole()))).ToActionResult();

    /// <summary>Updates an epic — title, description, status, order, or sprint assignment.</summary>
    [HttpPatch("{id:guid}")]
    [RequiresCapability(CapabilityRegistry.ProductManagerOrAbove)]
    [ProducesResponseType(typeof(ApiResponse<EpicDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdateEpic(Guid id, [FromBody] UpdateEpicRequest request) =>
        (await _mediator.Send(new UpdateEpicCommand(
            id, request.Title, request.Description, request.AcceptanceCriteria, request.Status,
            request.Order, request.SprintId, request.RemoveFromSprint,
            GetActorId(), GetIp(), GetRole()))).ToActionResult();

    /// <summary>Deletes an epic. Tasks under the epic lose their epic association but are not deleted.</summary>
    [HttpDelete("{id:guid}")]
    [RequiresCapability(CapabilityRegistry.ProductManagerOrAbove)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DeleteEpic(Guid id)
    {
        var result = await _mediator.Send(new DeleteEpicCommand(id, GetActorId(), GetIp(), GetRole()));
        return result.IsSuccess ? NoContent() : result.ToActionResult();
    }

    private Guid GetActorId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string GetRole() => User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;
    private string? GetIp() => HttpContext.Connection.RemoteIpAddress?.ToString();
}
