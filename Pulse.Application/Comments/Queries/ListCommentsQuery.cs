using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Comments.Queries;

public record ListCommentsQuery(Guid TaskId, Guid ActorId, string ActorRole) : IRequest<ServiceResult<IReadOnlyList<CommentDto>>>;

public class ListCommentsHandler : IRequestHandler<ListCommentsQuery, ServiceResult<IReadOnlyList<CommentDto>>>
{
    private readonly ITaskCommentRepository _comments;
    private readonly IEngineerRepository _engineers;
    private readonly IProjectAccessPolicy _access;

    public ListCommentsHandler(ITaskCommentRepository comments, IEngineerRepository engineers, IProjectAccessPolicy access)
    {
        _comments = comments;
        _engineers = engineers;
        _access = access;
    }

    public async Task<ServiceResult<IReadOnlyList<CommentDto>>> Handle(ListCommentsQuery request, CancellationToken ct)
    {
        if (!Roles.IsOrgReadOnlyViewer(request.ActorRole) && !await _access.CanViewTaskAsync(request.TaskId, request.ActorId, request.ActorRole, ct))
            return ServiceResult<IReadOnlyList<CommentDto>>.Fail("FORBIDDEN", "You do not have access to this task.");

        var comments = await _comments.GetByTaskAsync(request.TaskId, ct);
        var authorIds = comments.Select(c => c.AuthorId).Distinct().ToList();
        var engineers = await _engineers.GetByIdsAsync(authorIds, ct);
        var nameMap = engineers.ToDictionary(e => e.Id, e => e.Name);

        var dtos = comments
            .OrderBy(c => c.CreatedAt)
            .Select(c => CommentDto.From(c, nameMap.GetValueOrDefault(c.AuthorId, "Unknown")))
            .ToList();

        return ServiceResult<IReadOnlyList<CommentDto>>.Ok(dtos);
    }
}
