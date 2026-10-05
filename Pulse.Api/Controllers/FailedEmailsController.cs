using Asp.Versioning;
using Pulse.Api.Authorization;
using Pulse.Application.Auth;
using Pulse.Api.Common;
using Pulse.Application.Common;
using Pulse.Application.Email;
using Pulse.Application.Email.Commands;
using Pulse.Application.Email.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Pulse.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/failed-emails")]
[Tags("FailedEmails")]
[RequiresCapability(CapabilityRegistry.HeadOnly)]
public class FailedEmailsController : ControllerBase
{
    private readonly IMediator _mediator;

    public FailedEmailsController(IMediator mediator) => _mediator = mediator;

    /// <summary>Lists failed email records. Pass includeResolved=true to also show resolved/dismissed entries.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<FailedEmailPageDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(
        [FromQuery] bool includeResolved = false,
        [FromQuery] int limit = 25,
        [FromQuery] Guid? cursor = null) =>
        (await _mediator.Send(new ListFailedEmailsQuery(includeResolved, limit, cursor))).ToActionResult();

    /// <summary>Manually retries a single failed email. Marks it resolved on success.</summary>
    [HttpPost("{id:guid}/retry")]
    [ProducesResponseType(typeof(ApiResponse<FailedEmailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Retry(Guid id) =>
        (await _mediator.Send(new RetryFailedEmailCommand(id))).ToActionResult();

    /// <summary>Retries all unresolved failed emails. Returns count of succeeded and failed.</summary>
    [HttpPost("retry-all")]
    [ProducesResponseType(typeof(ApiResponse<RetryAllResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> RetryAll() =>
        (await _mediator.Send(new RetryAllFailedEmailsCommand())).ToActionResult();

    /// <summary>Dismisses (marks resolved) a failed email without retrying.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse<FailedEmailDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Dismiss(Guid id) =>
        (await _mediator.Send(new DismissFailedEmailCommand(id))).ToActionResult();
}
