using Pulse.Domain.Tasks;

namespace Pulse.Application.Common.Interfaces;

public interface IEstimationRepository
{
    Task<TaskEstimationSession?> GetSessionAsync(Guid taskId, CancellationToken ct = default);
    /// <summary>Sessions with a pending request submitted at or before <paramref name="cutoff"/>
    /// that haven't already escalated — candidates for EstimateApprovalEscalationScanner.</summary>
    Task<IReadOnlyList<TaskEstimationSession>> GetPendingApprovalsOlderThanAsync(DateTime cutoff, CancellationToken ct = default);
    Task<IReadOnlyList<TaskEstimationVote>> GetVotesAsync(Guid taskId, CancellationToken ct = default);
    Task<TaskEstimationVote?> GetVoteAsync(Guid taskId, Guid voterId, CancellationToken ct = default);
    Task AddSessionAsync(TaskEstimationSession session, CancellationToken ct = default);
    Task AddVoteAsync(TaskEstimationVote vote, CancellationToken ct = default);
    Task DeleteSessionAndVotesAsync(Guid taskId, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
