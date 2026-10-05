using Pulse.Domain.Common;

namespace Pulse.Domain.Tasks;

/// <summary>A lightweight checklist item on a task — not a full PulseTask (no points, status,
/// assignee). Deliberately separate from PulseTask.ParentTaskId, which is a narrower mechanism
/// reserved for QA sub-tasks; reusing it here would pull checklist items into the QA queue query.</summary>
public class Subtask : Entity
{
    public Guid TaskId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public bool IsDone { get; private set; }
    public Guid CreatedBy { get; private set; }
    public Guid? CompletedBy { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    /// <summary>Who this checklist item is delegated to — set via Loan. Null means it's implicitly
    /// the parent task's own assignee, same as before this field existed.</summary>
    public Guid? AssigneeId { get; private set; }
    /// <summary>Who held this subtask immediately before the current loan — mirrors
    /// PulseTask.LoanedFromEngineerId. Null before any loan, or once Recall has restored it.</summary>
    public Guid? LoanedFromEngineerId { get; private set; }

    private Subtask() { }

    public static Subtask Create(Guid taskId, string title, Guid createdBy)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new DomainException("Subtask title is required.");

        return new Subtask
        {
            TaskId = taskId,
            Title = title.Trim(),
            CreatedBy = createdBy,
        };
    }

    public void SetDone(bool isDone, Guid actorId)
    {
        IsDone = isDone;
        CompletedBy = isDone ? actorId : null;
        CompletedAt = isDone ? DateTime.UtcNow : null;
    }

    /// <summary>Loans this subtask to <paramref name="engineerId"/>, remembering whoever held it
    /// immediately before so <see cref="Recall"/> can undo it — mirrors PulseTask.Loan. Loaning
    /// an already-loaned subtask again (without an intervening recall) is allowed, same as at the
    /// task level: each call only remembers one level back, so recalling after a re-loan restores
    /// the immediately previous holder, not the original one further back.</summary>
    public void Loan(Guid engineerId)
    {
        LoanedFromEngineerId = AssigneeId;
        AssigneeId = engineerId;
    }

    /// <summary>Reverses the most recent loan, restoring whoever held this subtask immediately
    /// before it — null if it had never been loaned before that.</summary>
    public void Recall()
    {
        if (AssigneeId is null)
            throw new DomainException("This subtask is not currently loaned to anyone.");
        AssigneeId = LoanedFromEngineerId;
        LoanedFromEngineerId = null;
    }
}
