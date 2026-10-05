using Pulse.Domain.Tasks;

namespace Pulse.Application.Comments;

public record CommentDto(
    Guid Id,
    Guid TaskId,
    Guid AuthorId,
    string AuthorName,
    string Body,
    DateTime CreatedAt,
    DateTime? EditedAt)
{
    public static CommentDto From(TaskComment c, string authorName) =>
        new(c.Id, c.TaskId, c.AuthorId, authorName, c.Body, c.CreatedAt, c.EditedAt);
}
