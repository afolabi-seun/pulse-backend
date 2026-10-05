using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Estimation.Commands;

public record ResetEstimationCommand(Guid TaskId) : IRequest<ServiceResult<Unit>>;

public class ResetEstimationHandler : IRequestHandler<ResetEstimationCommand, ServiceResult<Unit>>
{
    private readonly IEstimationRepository _estimation;

    public ResetEstimationHandler(IEstimationRepository estimation) => _estimation = estimation;

    public async Task<ServiceResult<Unit>> Handle(ResetEstimationCommand request, CancellationToken ct)
    {
        await _estimation.DeleteSessionAndVotesAsync(request.TaskId, ct);
        return ServiceResult<Unit>.Ok(Unit.Value);
    }
}
