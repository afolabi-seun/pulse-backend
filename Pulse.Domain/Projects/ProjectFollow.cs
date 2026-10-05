using Pulse.Domain.Common;

namespace Pulse.Domain.Projects;

public class ProjectFollow : Entity
{
    public Guid FollowerId { get; private set; }
    public Guid ProjectId  { get; private set; }

    private ProjectFollow() { }

    public static ProjectFollow Create(Guid followerId, Guid projectId) =>
        new() { FollowerId = followerId, ProjectId = projectId };
}
