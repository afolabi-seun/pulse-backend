namespace Pulse.Domain.Tasks;

public class TaskHistory
{
    public Guid Id { get; private set; }  // EF Core generates via ValueGeneratedOnAdd
    public Guid TaskId { get; private set; }
    public string Field { get; private set; } = string.Empty;
    public string? OldValue { get; private set; }
    public string? NewValue { get; private set; }
    public Guid ActorId { get; private set; }
    public DateTime ChangedAt { get; private set; } = DateTime.UtcNow;
    /// <summary>Set only on "assignee_id" entries written by <see cref="PulseTask.Loan"/>/
    /// <see cref="PulseTask.Recall"/> ("loaned"/"recalled") — distinguishes a loan/recall from a
    /// plain reassignment for the project Activity feed. Null for every other entry, including
    /// ordinary reassignment, so nothing that reads "assignee_id" for its own purposes (e.g. the
    /// most recent such entry as "assigned at") needs to change.</summary>
    public string? Context { get; private set; }
    /// <summary>Set only on the "status" -> "Done" entry written by <see cref="PulseTask.MarkDone"/>/
    /// <see cref="PulseTask.AcceptQa"/>/<see cref="PulseTask.SeedDoneAt"/> — a snapshot, taken at
    /// the moment the task actually finished, of which engineer's team should get delivery credit:
    /// the lending engineer if the task was on loan at completion, otherwise the engineer who most
    /// recently held the backend half of a frontend-handoff task, otherwise the current assignee.
    /// Needed because AssigneeId/LoanedFromEngineerId/BackendAssigneeId are all live fields that can
    /// keep changing after completion, so reporting can't reconstruct this after the fact from the
    /// task's current state. Null for every other entry.</summary>
    public Guid? CreditedEngineerId { get; private set; }

    /// <summary>Set only on "due_date" entries written when an existing due date is changed by a
    /// person (<see cref="PulseTask.UpdateDetails"/>, <see cref="PulseTask.ShiftDueDate"/>) — the
    /// reason they stated. Null for first-time sets, system-driven changes and entries that predate
    /// the mandatory-reason rule.</summary>
    public string? Reason { get; private set; }

    private TaskHistory() { }

    public static TaskHistory Record(Guid taskId, string field, string? oldValue, string? newValue, Guid actorId, string? context = null, Guid? creditedEngineerId = null, string? reason = null) =>
        new() { TaskId = taskId, Field = field, OldValue = oldValue, NewValue = newValue, ActorId = actorId, Context = context, CreditedEngineerId = creditedEngineerId, Reason = reason };

    public static TaskHistory RecordAt(Guid taskId, string field, string? oldValue, string? newValue, Guid actorId, DateTime changedAt, string? context = null, Guid? creditedEngineerId = null, string? reason = null) =>
        new() { TaskId = taskId, Field = field, OldValue = oldValue, NewValue = newValue, ActorId = actorId, ChangedAt = changedAt, Context = context, CreditedEngineerId = creditedEngineerId, Reason = reason };
}
