using Pulse.Domain.Tasks;

namespace Pulse.Application.Common.Interfaces;

public interface IEstimationRepository
{
    Task<TaskEstimationSession?> GetSessionAsync(Guid taskId, CancellationToken ct = default);
    /// <summary>Sessions with a pending request that has not yet gone all the way up to the PMO — the candidates for
    /// EstimateApprovalEscalationScanner, which decides from the working-hours clock which of them are due a next tier.</summary>
    Task<IReadOnlyList<TaskEstimationSession>> ListPendingApprovalsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TaskEstimationVote>> GetVotesAsync(Guid taskId, CancellationToken ct = default);
    Task<TaskEstimationVote?> GetVoteAsync(Guid taskId, Guid voterId, CancellationToken ct = default);
    Task AddSessionAsync(TaskEstimationSession session, CancellationToken ct = default);
    Task AddVoteAsync(TaskEstimationVote vote, CancellationToken ct = default);
    Task DeleteSessionAndVotesAsync(Guid taskId, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
