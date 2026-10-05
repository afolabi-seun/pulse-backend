using Pulse.Domain.Common;

namespace Pulse.Domain.Tasks;

/// <summary>Task B (DependentTaskId) is blocked until Task A (BlockingTaskId) is done.</summary>
public class TaskDependency : Entity
{
    public Guid BlockingTaskId { get; private set; }
    public Guid DependentTaskId { get; private set; }

    private TaskDependency() { }

    public static TaskDependency Create(Guid blockingTaskId, Guid dependentTaskId) =>
        new() { BlockingTaskId = blockingTaskId, DependentTaskId = dependentTaskId };
}
