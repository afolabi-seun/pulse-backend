using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.Thresholds;

public record DeleteDepartmentThresholdCommand(
    string Department,
    Guid ActorId,
    string? IpAddress) : IRequest<ServiceResult<bool>>;

public class DeleteDepartmentThresholdHandler : IRequestHandler<DeleteDepartmentThresholdCommand, ServiceResult<bool>>
{
    private readonly IDepartmentThresholdRepository _repo;
    private readonly IAuditLogRepository _audit;

    public DeleteDepartmentThresholdHandler(IDepartmentThresholdRepository repo, IAuditLogRepository audit)
    {
        _repo = repo;
        _audit = audit;
    }

    public async Task<ServiceResult<bool>> Handle(DeleteDepartmentThresholdCommand cmd, CancellationToken ct)
    {
        await _repo.DeleteAsync(cmd.Department, ct);

        await _audit.LogAsync("DEPARTMENT_THRESHOLD_DELETED", cmd.ActorId, cmd.IpAddress,
            $"Overwork threshold override removed for department '{cmd.Department}' — reverted to global default", ct);

        return ServiceResult<bool>.Ok(true);
    }
}
