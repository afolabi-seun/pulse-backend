using System.Security.Claims;
using Asp.Versioning;
using Pulse.Api.Attributes;
using Pulse.Api.Authorization;
using Pulse.Application.Auth;
using Pulse.Api.Common;
using Pulse.Application.Common;
using Pulse.Application.Estimation;
using Pulse.Application.Estimation.Commands;
using Pulse.Application.Estimation.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Pulse.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/tasks/{taskId:guid}/estimation")]
[Tags("Estimation")]
[EngineerAuth]
public class EstimationController : ControllerBase
{
    private readonly IMediator _mediator;

    public EstimationController(IMediator mediator) => _mediator = mediator;

    public record SubmitVoteRequest(int Points);
    public record SubmitEstimateForApprovalRequest(int Points);
    public record RejectEstimateRequest(string? Reason);

    /// <summary>Returns the current estimation session for a task.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<EstimationDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetEstimation(Guid taskId, CancellationToken ct)
    {
        var result = await _mediator.Send(new GetEstimationQuery(taskId, CurrentUserId(), CurrentRole()), ct);
        return result.ToActionResult();
    }

    /// <summary>Submit or update a vote for the current user.</summary>
    [HttpPost("vote")]
    [ProducesResponseType(typeof(ApiResponse<Unit>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<Unit>), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SubmitVote(Guid taskId, [FromBody] SubmitVoteRequest request, CancellationToken ct)
    {
        var result = await _mediator.Send(new SubmitVoteCommand(taskId, CurrentUserId(), request.Points, CurrentRole()), ct);
        return result.ToActionResult();
    }

    /// <summary>Reveal all votes (team lead or above).</summary>
    [HttpPost("reveal")]
    [RequiresCapability(CapabilityRegistry.TeamLeadOrAbove)]
    [ProducesResponseType(typeof(ApiResponse<Unit>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<Unit>), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RevealCards(Guid taskId, CancellationToken ct)
    {
        var result = await _mediator.Send(new RevealCardsCommand(taskId), ct);
        return result.ToActionResult();
    }

    /// <summary>Submit the revealed estimate for tiered approval — the task's own assignee, or a
    /// Team Lead+ on their behalf — no longer writes points directly; see accept/reject below.</summary>
    [HttpPost("submit-for-approval")]
    [ProducesResponseType(typeof(ApiResponse<Unit>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<Unit>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> SubmitForApproval(Guid taskId, [FromBody] SubmitEstimateForApprovalRequest request, CancellationToken ct)
    {
        var result = await _mediator.Send(new SubmitEstimateForApprovalCommand(taskId, request.Points, CurrentUserId(), CurrentRole()), ct);
        return result.ToActionResult();
    }

    /// <summary>Approve a pending estimate, writing its points to the task — the assignee's own
    /// Team Lead, escalating to also include the department head after a grace period, or a Team
    /// Lead+ fallback when neither can be resolved.</summary>
    [HttpPost("approve")]
    [ProducesResponseType(typeof(ApiResponse<Unit>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<Unit>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ApproveEstimate(Guid taskId, CancellationToken ct)
    {
        var result = await _mediator.Send(new ApproveEstimateCommand(taskId, CurrentUserId(), CurrentRole()), ct);
        return result.ToActionResult();
    }

    /// <summary>Reject a pending estimate — clears the request but leaves votes intact so the team
    /// can resubmit. Same authorization as approve.</summary>
    [HttpPost("reject")]
    [ProducesResponseType(typeof(ApiResponse<Unit>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<Unit>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> RejectEstimate(Guid taskId, [FromBody] RejectEstimateRequest request, CancellationToken ct)
    {
        var result = await _mediator.Send(new RejectEstimateCommand(taskId, request.Reason, CurrentUserId(), CurrentRole()), ct);
        return result.ToActionResult();
    }

    /// <summary>Reset the estimation session and clear all votes (team lead or above).</summary>
    [HttpDelete]
    [RequiresCapability(CapabilityRegistry.TeamLeadOrAbove)]
    [ProducesResponseType(typeof(ApiResponse<Unit>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ResetEstimation(Guid taskId, CancellationToken ct)
    {
        var result = await _mediator.Send(new ResetEstimationCommand(taskId), ct);
        return result.ToActionResult();
    }

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string CurrentRole() => User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;
}
