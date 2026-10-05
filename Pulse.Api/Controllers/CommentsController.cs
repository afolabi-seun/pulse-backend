using System.Security.Claims;
using Asp.Versioning;
using Pulse.Api.Attributes;
using Pulse.Api.Common;
using Pulse.Application.Comments;
using Pulse.Application.Common;
using Pulse.Application.Comments.Commands;
using Pulse.Application.Comments.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Unit = MediatR.Unit;

namespace Pulse.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/tasks/{taskId:guid}/comments")]
[Tags("Comments")]
[EngineerAuth]
public class CommentsController : ControllerBase
{
    private readonly IMediator _mediator;

    public CommentsController(IMediator mediator) => _mediator = mediator;

    public record AddCommentRequest(string Body);
    public record EditCommentRequest(string Body);

    /// <summary>Returns all comments for a task, ordered oldest first.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<CommentDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(Guid taskId, CancellationToken ct) =>
        (await _mediator.Send(new ListCommentsQuery(taskId, CurrentUserId(), CurrentRole()), ct)).ToActionResult();

    /// <summary>Adds a comment to a task.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<CommentDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<CommentDto>), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Add(Guid taskId, [FromBody] AddCommentRequest request, CancellationToken ct) =>
        (await _mediator.Send(new AddCommentCommand(taskId, CurrentUserId(), request.Body, CurrentRole()), ct)).ToActionResult();

    /// <summary>Edits a comment. Only the author may edit.</summary>
    [HttpPatch("{commentId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<Unit>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<Unit>), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Edit(Guid taskId, Guid commentId, [FromBody] EditCommentRequest request, CancellationToken ct) =>
        (await _mediator.Send(new EditCommentCommand(commentId, CurrentUserId(), request.Body), ct)).ToActionResult();

    /// <summary>Deletes a comment. Authors or team leads and above may delete.</summary>
    [HttpDelete("{commentId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<Unit>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Delete(Guid taskId, Guid commentId, CancellationToken ct) =>
        (await _mediator.Send(new DeleteCommentCommand(commentId, CurrentUserId(), CurrentRole()), ct)).ToActionResult();

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string CurrentRole() => User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;
}
