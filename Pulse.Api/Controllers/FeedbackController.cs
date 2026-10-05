using System.Security.Claims;
using Asp.Versioning;
using Pulse.Api.Attributes;
using Pulse.Api.Authorization;
using Pulse.Application.Auth;
using Pulse.Api.Common;
using Pulse.Application.Common;
using Pulse.Application.Feedback;
using Pulse.Application.Feedback.Commands;
using Pulse.Application.Feedback.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Pulse.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/feedback")]
[Tags("Feedback")]
[EngineerAuth]
public class FeedbackController : ControllerBase
{
    private readonly IMediator _mediator;

    public FeedbackController(IMediator mediator) => _mediator = mediator;

    public record SubmitFeedbackRequest(string Text, Guid? TaskId = null);
    public record ReplyToFeedbackRequest(string Text);

    /// <summary>Submits feedback for the current week.</summary>
    /// <remarks>
    /// Any authenticated engineer may submit feedback. Multiple submissions per week are allowed.
    /// Feedback is readable only by department heads.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<FeedbackDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SubmitFeedback([FromBody] SubmitFeedbackRequest request)
    {
        var result = await _mediator.Send(new SubmitFeedbackCommand(request.Text, GetActorId(), request.TaskId));
        return result.ToCreatedResult();
    }

    /// <summary>Returns all feedback entries. Any department head, plus HR (org-wide, anonymous —
    /// see ListFeedbackHandler). Every read is audit-logged.</summary>
    [HttpGet]
    [RequiresCapability(CapabilityRegistry.AnyHead, CapabilityRegistry.HrRead)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<FeedbackDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListFeedback([FromQuery] DateOnly? weekOf) =>
        (await _mediator.Send(new ListFeedbackQuery(weekOf, GetActorId(), GetCallerRole(), GetIp()))).ToActionResult();

    /// <summary>Returns anonymised weekly patterns — the same scope as the entries list (a department head's own
    /// department, everyone for PMO/HR). A week is shown only when ≥ 3 distinct people gave feedback; the rest are
    /// counted, not shown.</summary>
    [HttpGet("patterns")]
    [RequiresCapability(CapabilityRegistry.AnyHead, CapabilityRegistry.HrRead)]
    [ProducesResponseType(typeof(ApiResponse<FeedbackPatternsDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetPatterns() =>
        (await _mediator.Send(new GetFeedbackPatternsQuery(GetActorId(), GetCallerRole(), GetIp()))).ToActionResult();

    /// <summary>Sends a private reply to a feedback entry — delivered to the submitter as a
    /// notification, never a visible thread. Department heads only, scoped to their own
    /// department (HeadOfPmo is org-wide, like everywhere else). Not available to HR, whose
    /// read access is meant to stay anonymized.</summary>
    [HttpPost("{id:guid}/reply")]
    [RequiresCapability(CapabilityRegistry.AnyHead)]
    [ProducesResponseType(typeof(ApiResponse<FeedbackDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReplyToFeedback(Guid id, [FromBody] ReplyToFeedbackRequest request) =>
        (await _mediator.Send(new ReplyToFeedbackCommand(id, request.Text, GetActorId(), GetCallerRole(), GetIp()))).ToActionResult();

    /// <summary>Deletes a feedback entry submitted by the caller.</summary>
    /// <remarks>
    /// Engineers may only delete their own feedback. The deleted content is not recoverable.
    /// A SHA-256 hash of the deleted text is written to the audit log for accountability.
    /// </remarks>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteFeedback(Guid id) =>
        (await _mediator.Send(new DeleteFeedbackCommand(id, GetActorId(), GetIp()))).ToNoContentResult();

    private Guid GetActorId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string GetCallerRole() => User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;
    private string? GetIp() => HttpContext.Connection.RemoteIpAddress?.ToString();
}
