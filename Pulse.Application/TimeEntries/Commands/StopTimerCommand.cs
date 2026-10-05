using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.TimeEntries;
using MediatR;

namespace Pulse.Application.TimeEntries.Commands;

public record StopTimerCommand(Guid EngineerId, Guid ActorId, string? IpAddress) : IRequest<ServiceResult<TimeEntryDto>>;

public class StopTimerHandler : IRequestHandler<StopTimerCommand, ServiceResult<TimeEntryDto>>
{
    private readonly IActiveTimerRepository _timers;
    private readonly ITimeEntryRepository _timeEntries;
    private readonly IAuditLogRepository _audit;

    public StopTimerHandler(IActiveTimerRepository timers, ITimeEntryRepository timeEntries, IAuditLogRepository audit)
    {
        _timers = timers;
        _timeEntries = timeEntries;
        _audit = audit;
    }

    public async Task<ServiceResult<TimeEntryDto>> Handle(StopTimerCommand cmd, CancellationToken ct)
    {
        var timer = await _timers.GetByEngineerAsync(cmd.EngineerId, ct);
        if (timer is null)
            return ServiceResult<TimeEntryDto>.Fail("NOT_FOUND", "No timer is currently running.");

        // Floor at 0.01h: TimeEntry.Hours must be >0, and a timer stopped almost immediately after
        // it started could otherwise round to exactly 0. Cap at 24h: a stale/forgotten timer stops
        // cleanly and logs a max-length day rather than blocking Stop with a validation error.
        var hours = Math.Clamp(Math.Round((decimal)(DateTime.UtcNow - timer.StartedAt).TotalHours, 2), 0.01m, 24m);

        var entry = TimeEntry.Log(
            timer.EngineerId, DateOnly.FromDateTime(timer.StartedAt), timer.Category, timer.TaskId, null, hours, null, timer.SubtaskId);

        await _timeEntries.AddAsync(entry, ct);
        await _timers.DeleteAsync(timer, ct);
        await _timeEntries.SaveChangesAsync(ct);

        await _audit.LogAsync("TIME_ENTRY_TIMER_STOPPED", cmd.ActorId, cmd.IpAddress,
            $"Engineer {cmd.EngineerId} stopped a {timer.Category} timer ({hours}h)", ct);

        return ServiceResult<TimeEntryDto>.Ok(TimeEntryDto.From(entry));
    }
}
