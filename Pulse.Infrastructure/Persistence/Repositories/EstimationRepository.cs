using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.Persistence.Repositories;

public class EstimationRepository : IEstimationRepository
{
    private readonly PulseDbContext _db;

    public EstimationRepository(PulseDbContext db) => _db = db;

    public async Task<TaskEstimationSession?> GetSessionAsync(Guid taskId, CancellationToken ct = default) =>
        await _db.EstimationSessions.FirstOrDefaultAsync(s => s.TaskId == taskId, ct);

    public async Task<IReadOnlyList<TaskEstimationSession>> GetPendingApprovalsOlderThanAsync(DateTime cutoff, CancellationToken ct = default) =>
        await _db.EstimationSessions
            .Where(s => s.PendingApprovalPoints != null && !s.EscalatedToHead && s.SubmittedAt <= cutoff)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<TaskEstimationVote>> GetVotesAsync(Guid taskId, CancellationToken ct = default) =>
        await _db.EstimationVotes.Where(v => v.TaskId == taskId).ToListAsync(ct);

    public async Task<TaskEstimationVote?> GetVoteAsync(Guid taskId, Guid voterId, CancellationToken ct = default) =>
        await _db.EstimationVotes.FirstOrDefaultAsync(v => v.TaskId == taskId && v.VoterId == voterId, ct);

    public async Task AddSessionAsync(TaskEstimationSession session, CancellationToken ct = default) =>
        await _db.EstimationSessions.AddAsync(session, ct);

    public async Task AddVoteAsync(TaskEstimationVote vote, CancellationToken ct = default) =>
        await _db.EstimationVotes.AddAsync(vote, ct);

    public async Task DeleteSessionAndVotesAsync(Guid taskId, CancellationToken ct = default)
    {
        await _db.EstimationVotes.Where(v => v.TaskId == taskId).ExecuteDeleteAsync(ct);
        await _db.EstimationSessions.Where(s => s.TaskId == taskId).ExecuteDeleteAsync(ct);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default) =>
        await _db.SaveChangesAsync(ct);
}
