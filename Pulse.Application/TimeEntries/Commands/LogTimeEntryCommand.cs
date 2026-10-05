using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Common;
using Pulse.Domain.TimeEntries;
using MediatR;

namespace Pulse.Application.TimeEntries.Commands;

public record LogTimeEntryCommand(
    Guid EngineerId,
    DateOnly Date,
    TimeEntryCategory Category,
    Guid? TaskId,
    Guid? ProjectId,
    decimal Hours,
    string? Note,
    Guid ActorId,
    string? IpAddress) : IRequest<ServiceResult<TimeEntryDto>>;

public class LogTimeEntryHandler : IRequestHandler<LogTimeEntryCommand, ServiceResult<TimeEntryDto>>
{
    private readonly ITimeEntryRepository _timeEntries;
    private readonly ITaskRepository _tasks;
    private readonly IProjectRepository _projects;
    private readonly IAuditLogRepository _audit;

    public LogTimeEntryHandler(ITimeEntryRepository timeEntries, ITaskRepository tasks, IProjectRepository projects, IAuditLogRepository audit)
    {
        _timeEntries = timeEntries;
        _tasks = tasks;
        _projects = projects;
        _audit = audit;
    }

    public async Task<ServiceResult<TimeEntryDto>> Handle(LogTimeEntryCommand cmd, CancellationToken ct)
    {
        if (cmd.TaskId.HasValue)
        {
            var task = await _tasks.GetByIdAsync(cmd.TaskId.Value, ct);
            if (task is null)
                return ServiceResult<TimeEntryDto>.Fail("NOT_FOUND", $"Task '{cmd.TaskId}' not found.");

            try
            {
                task.EnsureCanLogTimeEntry();
            }
            catch (DomainException ex)
            {
                return ServiceResult<TimeEntryDto>.Fail("BUSINESS_RULE_VIOLATION", ex.Message);
            }
        }

        if (cmd.ProjectId.HasValue && await _projects.GetByIdAsync(cmd.ProjectId.Value, ct) is null)
            return ServiceResult<TimeEntryDto>.Fail("NOT_FOUND", $"Project '{cmd.ProjectId}' not found.");

        TimeEntry entry;
        try
        {
            entry = TimeEntry.Log(cmd.EngineerId, cmd.Date, cmd.Category, cmd.TaskId, cmd.ProjectId, cmd.Hours, cmd.Note);
        }
        catch (DomainException ex)
        {
            return ServiceResult<TimeEntryDto>.Fail("BUSINESS_RULE_VIOLATION", ex.Message);
        }

        await _timeEntries.AddAsync(entry, ct);
        await _timeEntries.SaveChangesAsync(ct);

        await _audit.LogAsync("TIME_ENTRY_LOGGED", cmd.ActorId, cmd.IpAddress,
            $"Engineer {cmd.EngineerId} logged {cmd.Hours}h for {cmd.Date:O}", ct);

        return ServiceResult<TimeEntryDto>.Ok(TimeEntryDto.From(entry));
    }
}
