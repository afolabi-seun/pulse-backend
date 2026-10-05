using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Tasks;
using MediatR;

namespace Pulse.Application.Estimation.Commands;

public record SubmitVoteCommand(Guid TaskId, Guid VoterId, int Points, string ActorRole = "") : IRequest<ServiceResult<Unit>>;

public class SubmitVoteHandler : IRequestHandler<SubmitVoteCommand, ServiceResult<Unit>>
{
    private static readonly int[] ValidPoints = [1, 2, 3, 5, 8, 13, 21];
    private readonly IEstimationRepository _estimation;
    private readonly IProjectAccessPolicy _access;

    public SubmitVoteHandler(IEstimationRepository estimation, IProjectAccessPolicy access)
    {
        _estimation = estimation;
        _access = access;
    }

    public async Task<ServiceResult<Unit>> Handle(SubmitVoteCommand request, CancellationToken ct)
    {
        if (!ValidPoints.Contains(request.Points))
            return ServiceResult<Unit>.Fail("INVALID_POINTS", $"Points must be one of: {string.Join(", ", ValidPoints)}.");

        if (!await _access.CanAccessTaskAsync(request.TaskId, request.VoterId, request.ActorRole, ct))
            return ServiceResult<Unit>.Fail("FORBIDDEN", "You do not have access to this task.");

        var session = await _estimation.GetSessionAsync(request.TaskId, ct);
        if (session is null)
        {
            var newSession = TaskEstimationSession.Create(request.TaskId);
            await _estimation.AddSessionAsync(newSession, ct);
        }
        else if (session.IsRevealed)
        {
            return ServiceResult<Unit>.Fail("SESSION_REVEALED", "Votes cannot be changed after cards are revealed.");
        }

        var existing = await _estimation.GetVoteAsync(request.TaskId, request.VoterId, ct);
        if (existing is not null)
            existing.UpdatePoints(request.Points);
        else
            await _estimation.AddVoteAsync(TaskEstimationVote.Create(request.TaskId, request.VoterId, request.Points), ct);

        await _estimation.SaveChangesAsync(ct);
        return ServiceResult<Unit>.Ok(Unit.Value);
    }
}
