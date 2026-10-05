namespace Pulse.Application.Estimation;

public record VoterVoteDto(Guid VoterId, string VoterName, int? Points);

public record EstimationDto(
    Guid TaskId,
    bool IsRevealed,
    IReadOnlyList<VoterVoteDto> Votes,
    int? PendingApprovalPoints = null,
    string? SubmittedByName = null,
    /// <summary>Who can currently approve/reject the pending request — empty when nothing is
    /// pending. Names, not ids: the viewer only needs to know who it's waiting on.</summary>
    IReadOnlyList<string>? PendingApproverNames = null,
    /// <summary>Whether the current viewer is one of the pending approvers — lets the frontend show
    /// Approve/Reject without duplicating EstimationApproval's department-head resolution.</summary>
    bool CanApprove = false,
    /// <summary>Whether the pending request has escalated past the Team Lead tier to also include
    /// the department head — see TaskEstimationSession.EscalatedToHead.</summary>
    bool IsEscalated = false);
