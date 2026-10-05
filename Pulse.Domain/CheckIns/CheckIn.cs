using Pulse.Domain.Common;

namespace Pulse.Domain.CheckIns;

public class CheckIn : Entity
{
    public Guid EngineerId { get; private set; }
    public DateOnly Date { get; private set; }
    public string Completed { get; private set; } = string.Empty;
    public string PlannedNext { get; private set; } = string.Empty;
    public string? Blockers { get; private set; }
    public Guid? ProjectId { get; private set; }

    private CheckIn() { }

    public static CheckIn Submit(Guid engineerId, DateOnly date, string completed, string plannedNext, string? blockers, Guid? projectId = null) =>
        new()
        {
            EngineerId = engineerId,
            Date = date,
            Completed = completed,
            PlannedNext = plannedNext,
            Blockers = blockers,
            ProjectId = projectId
        };

    public void Update(string completed, string plannedNext, string? blockers)
    {
        Completed = completed;
        PlannedNext = plannedNext;
        Blockers = blockers;
    }
}
