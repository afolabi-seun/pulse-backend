using Pulse.Domain.Common;

namespace Pulse.Domain.Tasks;

/// <summary>
/// One dated step in the points a task has earned: positive when progress moved forward or the estimate grew, negative when work was taken back (a QA
/// rejection, a task returned to the Backlog, an estimate cut). Summing a task's credits gives its earned points; summing them by week and engineer
/// gives earned throughput. Written centrally when a task is saved, never by hand.
/// </summary>
public class TaskProgressCredit : Entity
{
    public Guid TaskId { get; private set; }
    /// <summary>Who the points are credited to — the same engineer a finished task is credited to. Null for an unassigned task.</summary>
    public Guid? EngineerId { get; private set; }
    public decimal Points { get; private set; }
    /// <summary>The task's effective progress once this step is applied.</summary>
    public int ProgressAfter { get; private set; }
    public DateTime CreditedAt { get; private set; }

    private TaskProgressCredit() { }

    public static TaskProgressCredit Create(Guid taskId, Guid? engineerId, decimal points, int progressAfter, DateTime? at = null) =>
        new() { TaskId = taskId, EngineerId = engineerId, Points = points, ProgressAfter = progressAfter, CreditedAt = at ?? DateTime.UtcNow };
}
