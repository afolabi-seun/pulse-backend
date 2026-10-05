using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Estimation.Commands;

public record RevealCardsCommand(Guid TaskId) : IRequest<ServiceResult<Unit>>;

public class RevealCardsHandler : IRequestHandler<RevealCardsCommand, ServiceResult<Unit>>
{
    private readonly IEstimationRepository _estimation;

    public RevealCardsHandler(IEstimationRepository estimation) => _estimation = estimation;

    public async Task<ServiceResult<Unit>> Handle(RevealCardsCommand request, CancellationToken ct)
    {
        var session = await _estimation.GetSessionAsync(request.TaskId, ct);
        if (session is null)
            return ServiceResult<Unit>.Fail("NO_SESSION", "No estimation session exists for this task.");

        if (session.IsRevealed)
            return ServiceResult<Unit>.Ok(Unit.Value);

        session.Reveal();
        await _estimation.SaveChangesAsync(ct);
        return ServiceResult<Unit>.Ok(Unit.Value);
    }
}
