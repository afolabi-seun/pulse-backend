using System.Security.Claims;
using Asp.Versioning;
using Pulse.Api.Attributes;
using Pulse.Api.Authorization;
using Pulse.Api.Reports;
using Pulse.Application.Auth;
using Pulse.Api.Common;
using Pulse.Application.CheckIns;
using Pulse.Application.CheckIns.Commands;
using Pulse.Application.CheckIns.Queries;
using Pulse.Application.Common;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Pulse.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/check-ins")]
[Tags("Check-ins")]
[EngineerAuth]
public class CheckInsController : ControllerBase
{
    private readonly IMediator _mediator;

    public CheckInsController(IMediator mediator) => _mediator = mediator;

    public record SubmitCheckInRequest(
        DateOnly Date,
        string Completed,
        string PlannedNext,
        string? Blockers,
        Guid? ProjectId = null);

    /// <summary>Returns check-in history for an engineer.</summary>
    /// <remarks>
    /// Engineers always see their own history only.
    /// Team leads and above may pass engineerId to view another engineer's history.
    /// Results are ordered by date descending (most recent first).
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<PagedResultDto<CheckInDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ListCheckIns(
        [FromQuery] Guid? engineerId,
        [FromQuery] int limit = 25,
        [FromQuery] string? cursor = null) =>
        (await _mediator.Send(new ListCheckInsQuery(
            GetActorId(), GetRole(), engineerId, limit, cursor))).ToActionResult();

    /// <summary>Submits (or updates) the authenticated engineer's check-in for a given date.</summary>
    /// <remarks>
    /// One check-in per engineer per calendar date (per project, if scoped to one). Submitting again
    /// for the same date/project updates the existing entry rather than creating a duplicate.
    /// Date defaults to today if omitted.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<CheckInDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SubmitCheckIn([FromBody] SubmitCheckInRequest request)
    {
        var actorId = GetActorId();
        var result = await _mediator.Send(new SubmitCheckInCommand(
            actorId,
            request.Date,
            request.Completed,
            request.PlannedNext,
            request.Blockers,
            actorId,
            GetIp(),
            request.ProjectId));

        return result.ToActionResult();
    }

    /// <summary>Returns a single check-in by ID.</summary>
    [HttpGet("{id:guid}", Name = "GetCheckIn")]
    [ProducesResponseType(typeof(ApiResponse<CheckInDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetCheckIn(Guid id) =>
        (await _mediator.Send(new GetCheckInQuery(id, GetActorId(), GetRole()))).ToActionResult();

    /// <summary>Returns check-in status for all active engineers on a given date (defaults to today).</summary>
    [HttpGet("status")]
    [RequiresCapability(CapabilityRegistry.PmOrAbove)]
    [ProducesResponseType(typeof(ApiResponse<CheckInStatusDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetCheckInStatus([FromQuery] DateOnly? date = null) =>
        (await _mediator.Send(new GetCheckInStatusQuery(
            date ?? DateOnly.FromDateTime(DateTime.UtcNow), GetRole(), GetActorId()))).ToActionResult();

    /// <summary>Returns a standup summary scoped to the caller's visibility.</summary>
    /// <remarks>
    /// PMO sees all engineers. Dept heads see their department. Team leads see their own team only.
    /// The optional teamId filter narrows within the caller's already-scoped set.
    /// </remarks>
    [HttpGet("standup")]
    [RequiresCapability(CapabilityRegistry.TeamLeadOrAbove, CapabilityRegistry.ExecutiveRead, CapabilityRegistry.HrRead, CapabilityRegistry.AccountantRead)]
    [ProducesResponseType(typeof(ApiResponse<StandupSummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetStandupSummary(
        [FromQuery] Guid? teamId, [FromQuery] DateOnly? date, [FromQuery] int limit = 25, [FromQuery] string? cursor = null) =>
        (await _mediator.Send(new GetStandupSummaryQuery(teamId, date, GetRole(), GetActorId(), limit, cursor))).ToActionResult();

    /// <summary>Downloads the standup digest for a given day as a detailed CSV file.</summary>
    /// <remarks>Same access and scoping as the JSON endpoint above. Every check-in for the day is
    /// included (no pagination), plus a section listing who hasn't checked in.</remarks>
    [HttpGet("standup/csv")]
    [RequiresCapability(CapabilityRegistry.TeamLeadOrAbove, CapabilityRegistry.ExecutiveRead, CapabilityRegistry.HrRead, CapabilityRegistry.AccountantRead)]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetStandupSummaryCsv([FromQuery] Guid? teamId, [FromQuery] DateOnly? date)
    {
        var result = await _mediator.Send(new GetStandupSummaryQuery(teamId, date, GetRole(), GetActorId(), All: true));
        if (!result.IsSuccess)
            return result.ToActionResult();

        var csv = StandupSummaryCsvRenderer.Render(result.Data!);
        var filename = $"standup-digest-{result.Data!.Date:yyyy-MM-dd}.csv";
        return File(csv, "text/csv", filename);
    }

    private Guid GetActorId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string GetRole() => User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;
    private string? GetIp() => HttpContext.Connection.RemoteIpAddress?.ToString();
}
