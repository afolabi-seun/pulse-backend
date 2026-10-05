namespace Pulse.Domain.Projects;

public class ProjectMember
{
    public Guid ProjectId   { get; private set; }
    public Guid EngineerId  { get; private set; }
    public DateTime AddedAt { get; private set; }

    private ProjectMember() { }

    public static ProjectMember Create(Guid projectId, Guid engineerId) =>
        new() { ProjectId = projectId, EngineerId = engineerId, AddedAt = DateTime.UtcNow };
}
