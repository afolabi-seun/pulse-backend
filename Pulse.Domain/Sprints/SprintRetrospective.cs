using Pulse.Domain.Common;

namespace Pulse.Domain.Sprints;

public class SprintRetrospective : Entity
{
    public Guid SprintId { get; private set; }
    public string WentWell { get; private set; } = string.Empty;
    public string NeedsImprovement { get; private set; } = string.Empty;
    public string ActionItems { get; private set; } = string.Empty;
    public Guid CreatedById { get; private set; }
    public DateTime? UpdatedAt { get; private set; }

    private SprintRetrospective() { }

    public static SprintRetrospective Create(Guid sprintId, Guid createdById,
        string wentWell, string needsImprovement, string actionItems) =>
        new()
        {
            SprintId         = sprintId,
            CreatedById      = createdById,
            WentWell         = wentWell.Trim(),
            NeedsImprovement = needsImprovement.Trim(),
            ActionItems      = actionItems.Trim(),
        };

    public void Update(string wentWell, string needsImprovement, string actionItems)
    {
        WentWell         = wentWell.Trim();
        NeedsImprovement = needsImprovement.Trim();
        ActionItems      = actionItems.Trim();
        UpdatedAt        = DateTime.UtcNow;
    }
}
