using Pulse.Domain.Common;

namespace Pulse.Domain.Escalations;

public class EscalationEvent : Entity
{
    public Guid TaskId { get; private set; }
    public EscalationLevel Level { get; private set; }
    public DateTime FiredAt { get; private set; } = DateTime.UtcNow;

    private EscalationEvent() { }

    public static EscalationEvent Record(Guid taskId, EscalationLevel level) =>
        new() { TaskId = taskId, Level = level };
}
