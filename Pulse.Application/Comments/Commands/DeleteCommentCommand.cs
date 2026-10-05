using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Comments.Commands;

public record DeleteCommentCommand(Guid CommentId, Guid RequesterId, string RequesterRole) : IRequest<ServiceResult<Unit>>;

public class DeleteCommentHandler : IRequestHandler<DeleteCommentCommand, ServiceResult<Unit>>
{
    private readonly ITaskCommentRepository _comments;

    public DeleteCommentHandler(ITaskCommentRepository comments) => _comments = comments;

    public async Task<ServiceResult<Unit>> Handle(DeleteCommentCommand request, CancellationToken ct)
    {
        var comment = await _comments.GetByIdAsync(request.CommentId, ct);
        if (comment is null)
            return ServiceResult<Unit>.Fail("NOT_FOUND", "Comment not found.");

        var isOwner = comment.AuthorId == request.RequesterId;
        var isLead = request.RequesterRole is Roles.TeamLead or Roles.ProductManager
            or Roles.ProjectManager || Roles.HeadRoles.Contains(request.RequesterRole);

        if (!isOwner && !isLead)
            return ServiceResult<Unit>.Fail("FORBIDDEN", "You cannot delete this comment.");

        await _comments.DeleteAsync(comment, ct);
        await _comments.SaveChangesAsync(ct);
        return ServiceResult<Unit>.Ok(Unit.Value);
    }
}
