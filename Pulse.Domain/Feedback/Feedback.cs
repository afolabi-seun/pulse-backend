using Pulse.Domain.Common;

namespace Pulse.Domain.Feedback;

public class Feedback : Entity
{
    public Guid EngineerId { get; private set; }
    public string Text { get; private set; } = string.Empty;
    public DateOnly WeekOf { get; private set; }
    public Guid? TaskId { get; private set; }
    /// <summary>A private reply from a department head, delivered to the submitter as a
    /// notification — never a visible public thread other engineers can see. At most one reply;
    /// sending again overwrites the previous text rather than appending to a thread, matching the
    /// "private note back," not "open conversation," design this exists for.</summary>
    public string? ReplyText { get; private set; }
    public Guid? RepliedBy { get; private set; }
    public DateTime? RepliedAt { get; private set; }

    private Feedback() { }

    public static Feedback Submit(Guid engineerId, string text, DateOnly weekOf, Guid? taskId = null) =>
        new() { EngineerId = engineerId, Text = text, WeekOf = weekOf, TaskId = taskId };

    public void Reply(string text, Guid repliedBy)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new DomainException("Reply text is required.");

        ReplyText = text.Trim();
        RepliedBy = repliedBy;
        RepliedAt = DateTime.UtcNow;
    }
}
