using Pulse.Application.Auth;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Estimation.Queries;

public record GetEstimationQuery(Guid TaskId, Guid ActorId, string ActorRole) : IRequest<ServiceResult<EstimationDto>>;

public class GetEstimationHandler : IRequestHandler<GetEstimationQuery, ServiceResult<EstimationDto>>
{
    private readonly IEstimationRepository _estimation;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;
    private readonly ITaskRepository _tasks;
    private readonly IProjectAccessPolicy _access;

    public GetEstimationHandler(IEstimationRepository estimation, IEngineerRepository engineers, ITeamRepository teams, ITaskRepository tasks, IProjectAccessPolicy access)
    {
        _estimation = estimation;
        _engineers = engineers;
        _teams = teams;
        _tasks = tasks;
        _access = access;
    }

    public async Task<ServiceResult<EstimationDto>> Handle(GetEstimationQuery request, CancellationToken ct)
    {
        if (!Roles.IsOrgReadOnlyViewer(request.ActorRole) && !await _access.CanViewTaskAsync(request.TaskId, request.ActorId, request.ActorRole, ct))
            return ServiceResult<EstimationDto>.Fail("FORBIDDEN", "You do not have access to this task.");

        var session = await _estimation.GetSessionAsync(request.TaskId, ct);
        if (session is null)
            return ServiceResult<EstimationDto>.Ok(new EstimationDto(request.TaskId, false, []));

        var votes = await _estimation.GetVotesAsync(request.TaskId, ct);
        var voterIds = votes.Select(v => v.VoterId).Distinct().ToList();
        var engineers = await _engineers.GetByIdsAsync(voterIds, ct);
        var nameMap = engineers.ToDictionary(e => e.Id, e => e.Name);

        var voterVotes = votes
            .Select(v => new VoterVoteDto(
                v.VoterId,
                nameMap.GetValueOrDefault(v.VoterId, "Unknown"),
                session.IsRevealed ? v.Points : null))
            .ToList();

        string? submittedByName = null;
        IReadOnlyList<string>? approverNames = null;
        bool canApprove = false;
        if (session.PendingApprovalPoints.HasValue)
        {
            if (session.SubmittedBy is Guid submittedBy)
            {
                var submitter = await _engineers.GetByIdAsync(submittedBy, ct);
                submittedByName = submitter?.Name;
            }

            var task = await _tasks.GetByIdAsync(request.TaskId, ct);
            var state = await EstimationApproval.ResolveApproversAsync(task?.AssigneeId, session.EscalatedToHead, session.SubmittedBy, _engineers, _teams, ct);
            approverNames = state?.Approvers.Select(a => a.Name).ToList();
            canApprove = request.ActorId != session.SubmittedBy && (state is null || state.Approvers.Count == 0
                ? CapabilityRegistry.All[CapabilityRegistry.TeamLeadOrAbove].AllowedRoles.Contains(request.ActorRole)
                : state.Approvers.Any(a => a.Id == request.ActorId));
        }

        return ServiceResult<EstimationDto>.Ok(new EstimationDto(
            request.TaskId, session.IsRevealed, voterVotes,
            session.PendingApprovalPoints, submittedByName, approverNames, canApprove, session.EscalatedToHead));
    }
}
