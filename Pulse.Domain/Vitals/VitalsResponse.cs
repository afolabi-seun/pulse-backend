using Pulse.Domain.Common;

namespace Pulse.Domain.Vitals;

public class VitalsResponse : Entity
{
    public Guid EngineerId { get; private set; }
    public int Score { get; private set; }
    public string? Comment { get; private set; }
    public DateOnly WeekOf { get; private set; }

    private VitalsResponse() { }

    public static VitalsResponse Submit(Guid engineerId, int score, string? comment, DateOnly weekOf)
    {
        if (score is < 1 or > 5)
            throw new ArgumentOutOfRangeException(nameof(score), "Pulse score must be between 1 and 5.");

        return new() { EngineerId = engineerId, Score = score, Comment = comment, WeekOf = weekOf };
    }
}
