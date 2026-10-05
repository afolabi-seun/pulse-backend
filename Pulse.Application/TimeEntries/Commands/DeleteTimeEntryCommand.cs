using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using MediatR;

namespace Pulse.Application.TimeEntries.Commands;

public record DeleteTimeEntryCommand(Guid TimeEntryId, Guid ActorId, string? IpAddress) : IRequest<ServiceResult<Unit>>;

public class DeleteTimeEntryHandler : IRequestHandler<DeleteTimeEntryCommand, ServiceResult<Unit>>
{
    private readonly ITimeEntryRepository _timeEntries;
    private readonly IAuditLogRepository _audit;

    public DeleteTimeEntryHandler(ITimeEntryRepository timeEntries, IAuditLogRepository audit)
    {
        _timeEntries = timeEntries;
        _audit = audit;
    }

    public async Task<ServiceResult<Unit>> Handle(DeleteTimeEntryCommand cmd, CancellationToken ct)
    {
        var entry = await _timeEntries.GetByIdAsync(cmd.TimeEntryId, ct);
        if (entry is null)
            return ServiceResult<Unit>.Fail("NOT_FOUND", "Time entry not found.");

        if (entry.EngineerId != cmd.ActorId)
            return ServiceResult<Unit>.Fail("FORBIDDEN", "You can only delete your own time entries.");

        await _timeEntries.DeleteAsync(entry, ct);
        await _timeEntries.SaveChangesAsync(ct);

        await _audit.LogAsync("TIME_ENTRY_DELETED", cmd.ActorId, cmd.IpAddress,
            $"Engineer {cmd.ActorId} deleted time entry {entry.Id}", ct);

        return ServiceResult<Unit>.Ok(Unit.Value);
    }
}
