namespace Pulse.Domain.Tasks;

public class TaskEstimationVote
{
    public Guid Id { get; private set; }
    public Guid TaskId { get; private set; }
    public Guid VoterId { get; private set; }
    public int Points { get; private set; }
    public DateTime SubmittedAt { get; private set; }

    private TaskEstimationVote() { }

    public static TaskEstimationVote Create(Guid taskId, Guid voterId, int points) => new()
    {
        Id = Guid.NewGuid(),
        TaskId = taskId,
        VoterId = voterId,
        Points = points,
        SubmittedAt = DateTime.UtcNow,
    };

    public void UpdatePoints(int points)
    {
        Points = points;
        SubmittedAt = DateTime.UtcNow;
    }
}
