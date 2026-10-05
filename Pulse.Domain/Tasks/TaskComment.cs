using Pulse.Domain.Common;

namespace Pulse.Domain.Tasks;

public class TaskComment : Entity
{
    public Guid TaskId { get; private set; }
    public Guid AuthorId { get; private set; }
    public string Body { get; private set; } = string.Empty;
    public DateTime? EditedAt { get; private set; }

    private TaskComment() { }

    public static TaskComment Create(Guid taskId, Guid authorId, string body) =>
        new() { TaskId = taskId, AuthorId = authorId, Body = body.Trim() };

    public void Edit(string body)
    {
        Body = body.Trim();
        EditedAt = DateTime.UtcNow;
    }
}
