using Pulse.Domain.Common;

namespace Pulse.Domain.Tasks;

public class TaskEstimationSession
{
    public Guid TaskId { get; private set; }
    public bool IsRevealed { get; private set; }
    public DateTime CreatedAt { get; private set; }
    /// <summary>The points a Team Lead+ submitted for department-head approval — set by
    /// <see cref="SubmitForApproval"/>. On approval the whole session is deleted (the points are
    /// now on the task itself); on rejection <see cref="ClearApprovalRequest"/> clears just this,
    /// leaving votes/reveal intact. Null means no request is currently pending.</summary>
    public int? PendingApprovalPoints { get; private set; }
    public Guid? SubmittedBy { get; private set; }
    public DateTime? SubmittedAt { get; private set; }
    /// <summary>Whether this pending request has escalated past the Team Lead tier to also include
    /// the department head — set by <see cref="EscalateToHead"/>, reset on every fresh
    /// <see cref="SubmitForApproval"/>. Independent of the due-date EscalationEvent table — see
    /// planning-poker-approval-tiers-spec.md for why that table isn't reused here.</summary>
    public bool EscalatedToHead { get; private set; }

    private TaskEstimationSession() { }

    public static TaskEstimationSession Create(Guid taskId) => new()
    {
        TaskId = taskId,
        IsRevealed = false,
        CreatedAt = DateTime.UtcNow,
    };

    public void Reveal()
    {
        if (PendingApprovalPoints.HasValue)
            throw new DomainException("This estimate is already pending department-head approval.");
        IsRevealed = true;
    }

    /// <summary>Team Lead+ submits the accepted card value for the assignee's department head to
    /// approve — this no longer writes points directly (see AcceptEstimateHandler's old behavior).</summary>
    public void SubmitForApproval(int points, Guid actorId)
    {
        if (!IsRevealed)
            throw new DomainException("Cards must be revealed before submitting an estimate for approval.");
        if (PendingApprovalPoints.HasValue)
            throw new DomainException("An estimate is already pending approval for this task.");

        PendingApprovalPoints = points;
        SubmittedBy = actorId;
        SubmittedAt = DateTime.UtcNow;
        EscalatedToHead = false;
    }

    /// <summary>Opens up approval to the department head in addition to the Team Lead, once the
    /// Team Lead hasn't acted within the configured grace period — see
    /// EstimateApprovalEscalationScanner. The Team Lead keeps the ability to approve/reject too;
    /// this adds a backstop rather than transferring the request.</summary>
    public void EscalateToHead()
    {
        if (!PendingApprovalPoints.HasValue)
            throw new DomainException("No estimate is pending approval for this task.");
        EscalatedToHead = true;
    }

    /// <summary>Clears the pending request without resetting votes/reveal — used when a rejection
    /// leaves the session otherwise intact so the team can resubmit after discussion.</summary>
    public void ClearApprovalRequest()
    {
        PendingApprovalPoints = null;
        SubmittedBy = null;
        SubmittedAt = null;
        EscalatedToHead = false;
    }
}
