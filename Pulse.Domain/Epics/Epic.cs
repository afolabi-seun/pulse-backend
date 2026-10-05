using Pulse.Domain.Common;

namespace Pulse.Domain.Epics;

public class Epic : Entity
{
    public string Title { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public string? AcceptanceCriteria { get; private set; }
    public EpicStatus Status { get; private set; } = EpicStatus.NotStarted;
    public Guid ProjectId { get; private set; }
    public Guid? SprintId { get; private set; }
    public int Order { get; private set; }

    private Epic() { }

    public static Epic Create(string title, Guid projectId, string? description = null, int order = 0) =>
        new() { Title = title, ProjectId = projectId, Description = description, Order = order };

    public void Update(string title, string? description, string? acceptanceCriteria, int order)
    {
        Title = title;
        Description = description;
        AcceptanceCriteria = acceptanceCriteria;
        Order = order;
    }

    public void SetStatus(EpicStatus status) => Status = status;

    public void AssignToSprint(Guid sprintId) => SprintId = sprintId;

    public void RemoveFromSprint() => SprintId = null;
}
