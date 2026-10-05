using System.Security.Claims;
using Asp.Versioning;
using Pulse.Api.Attributes;
using Pulse.Api.Authorization;
using Pulse.Application.Auth;
using Pulse.Api.Common;
using Pulse.Application.Common;
using Pulse.Application.Sprints;
using Pulse.Application.Sprints.Commands;
using Pulse.Application.Sprints.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Pulse.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/sprints")]
[Tags("Sprints")]
[EngineerAuth]
public class SprintsController : ControllerBase
{
    private readonly IMediator _mediator;

    public SprintsController(IMediator mediator) => _mediator = mediator;

    public record CreateSprintRequest(
        Guid ProjectId,
        string Name,
        string? Goal,
        DateOnly StartDate,
        DateOnly EndDate);

    public record UpdateSprintRequest(
        string? Name,
        string? Goal,
        DateOnly? StartDate,
        DateOnly? EndDate,
        bool? Activate,
        bool? Complete,
        int? CapacityPoints,
        DateOnly? ShowAndTellDate,
        string? ShowAndTellNotes,
        string? DueDateChangeReason = null);

    /// <summary>Returns sprints, optionally filtered by team and/or project.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<SprintDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListSprints([FromQuery] Guid? teamId, [FromQuery] Guid? projectId) =>
        (await _mediator.Send(new ListSprintsQuery(teamId, projectId, GetActorId(), GetRole()))).ToActionResult();

    /// <summary>Creates a new sprint for the given project. The sprint's team is derived from the project's owning team.</summary>
    [HttpPost]
    [RequiresCapability(CapabilityRegistry.SprintCreatorOrAbove)]
    [ProducesResponseType(typeof(ApiResponse<SprintDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> CreateSprint([FromBody] CreateSprintRequest request)
    {
        var result = await _mediator.Send(new CreateSprintCommand(
            request.ProjectId, request.Name, request.Goal,
            request.StartDate, request.EndDate,
            GetActorId(), GetIp()));

        return result.ToCreatedResult("GetSprint", new { id = result.Data?.Id });
    }

    /// <summary>Returns a single sprint by ID.</summary>
    [HttpGet("{id:guid}", Name = "GetSprint")]
    [ProducesResponseType(typeof(ApiResponse<SprintDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetSprint(Guid id) =>
        (await _mediator.Send(new GetSprintQuery(id, GetActorId(), GetRole()))).ToActionResult();

    /// <summary>Partially updates a sprint — name, goal, dates, or transitions to Active or Completed.</summary>
    /// <remarks>
    /// All fields are optional. Activate=true transitions Planning→Active; Complete=true transitions Active→Completed.
    /// Cannot update a Completed sprint's details.
    /// </remarks>
    [HttpPatch("{id:guid}")]
    [RequiresCapability(CapabilityRegistry.SprintCreatorOrAbove)]
    [ProducesResponseType(typeof(ApiResponse<SprintDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> UpdateSprint(Guid id, [FromBody] UpdateSprintRequest request) =>
        (await _mediator.Send(new UpdateSprintCommand(
            id, request.Name, request.Goal, request.StartDate, request.EndDate,
            request.Activate, request.Complete,
            request.CapacityPoints, request.ShowAndTellDate, request.ShowAndTellNotes,
            GetActorId(), GetIp(), GetRole(), request.DueDateChangeReason))).ToActionResult();

    /// <summary>Deletes a sprint. Only Planning-status sprints can be deleted.</summary>
    [HttpDelete("{id:guid}")]
    [RequiresCapability(CapabilityRegistry.AnyHead)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DeleteSprint(Guid id)
    {
        var result = await _mediator.Send(new DeleteSprintCommand(id, GetActorId(), GetIp(), GetRole()));
        return result.IsSuccess ? NoContent() : result.ToActionResult();
    }

    /// <summary>Returns velocity metrics for a sprint — planned vs delivered story points.</summary>
    [HttpGet("{id:guid}/velocity")]
    [ProducesResponseType(typeof(ApiResponse<SprintVelocityDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetVelocity(Guid id) =>
        (await _mediator.Send(new GetSprintVelocityQuery(id, GetActorId(), GetRole()))).ToActionResult();

    /// <summary>Returns day-by-day burndown data for a sprint.</summary>
    [HttpGet("{id:guid}/burndown")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<BurndownPointDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBurndown(Guid id) =>
        (await _mediator.Send(new GetSprintBurndownQuery(id, GetActorId(), GetRole()))).ToActionResult();

    private Guid GetActorId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string GetRole() => User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;
    private string? GetIp() => HttpContext.Connection.RemoteIpAddress?.ToString();
}
