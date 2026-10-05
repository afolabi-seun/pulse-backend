using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Comments.Commands;

public record EditCommentCommand(Guid CommentId, Guid RequesterId, string Body) : IRequest<ServiceResult<CommentDto>>;

public class EditCommentHandler : IRequestHandler<EditCommentCommand, ServiceResult<CommentDto>>
{
    private readonly ITaskCommentRepository _comments;
    private readonly IEngineerRepository _engineers;

    public EditCommentHandler(ITaskCommentRepository comments, IEngineerRepository engineers)
    {
        _comments = comments;
        _engineers = engineers;
    }

    public async Task<ServiceResult<CommentDto>> Handle(EditCommentCommand request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Body))
            return ServiceResult<CommentDto>.Fail("EMPTY_BODY", "Comment cannot be empty.");

        if (request.Body.Length > 2000)
            return ServiceResult<CommentDto>.Fail("TOO_LONG", "Comment must be 2000 characters or fewer.");

        var comment = await _comments.GetByIdAsync(request.CommentId, ct);
        if (comment is null)
            return ServiceResult<CommentDto>.Fail("NOT_FOUND", "Comment not found.");

        if (comment.AuthorId != request.RequesterId)
            return ServiceResult<CommentDto>.Fail("FORBIDDEN", "You can only edit your own comments.");

        var author = await _engineers.GetByIdAsync(comment.AuthorId, ct);

        comment.Edit(request.Body);
        await _comments.SaveChangesAsync(ct);
        return ServiceResult<CommentDto>.Ok(CommentDto.From(comment, author?.Name ?? string.Empty));
    }
}
