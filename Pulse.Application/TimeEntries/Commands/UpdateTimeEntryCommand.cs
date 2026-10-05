using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Common;
using Pulse.Domain.TimeEntries;
using MediatR;

namespace Pulse.Application.TimeEntries.Commands;

public record UpdateTimeEntryCommand(
    Guid TimeEntryId,
    DateOnly Date,
    TimeEntryCategory Category,
    Guid? TaskId,
    Guid? ProjectId,
    decimal Hours,
    string? Note,
    Guid ActorId,
    string? IpAddress) : IRequest<ServiceResult<TimeEntryDto>>;

public class UpdateTimeEntryHandler : IRequestHandler<UpdateTimeEntryCommand, ServiceResult<TimeEntryDto>>
{
    private readonly ITimeEntryRepository _timeEntries;
    private readonly ITaskRepository _tasks;
    private readonly IProjectRepository _projects;
    private readonly IAuditLogRepository _audit;

    public UpdateTimeEntryHandler(ITimeEntryRepository timeEntries, ITaskRepository tasks, IProjectRepository projects, IAuditLogRepository audit)
    {
        _timeEntries = timeEntries;
        _tasks = tasks;
        _projects = projects;
        _audit = audit;
    }

    public async Task<ServiceResult<TimeEntryDto>> Handle(UpdateTimeEntryCommand cmd, CancellationToken ct)
    {
        var entry = await _timeEntries.GetByIdAsync(cmd.TimeEntryId, ct);
        if (entry is null)
            return ServiceResult<TimeEntryDto>.Fail("NOT_FOUND", $"Time entry '{cmd.TimeEntryId}' not found.");

        if (entry.EngineerId != cmd.ActorId)
            return ServiceResult<TimeEntryDto>.Fail("FORBIDDEN", "You can only edit your own time entries.");

        if (cmd.TaskId.HasValue && await _tasks.GetByIdAsync(cmd.TaskId.Value, ct) is null)
            return ServiceResult<TimeEntryDto>.Fail("NOT_FOUND", $"Task '{cmd.TaskId}' not found.");

        if (cmd.ProjectId.HasValue && await _projects.GetByIdAsync(cmd.ProjectId.Value, ct) is null)
            return ServiceResult<TimeEntryDto>.Fail("NOT_FOUND", $"Project '{cmd.ProjectId}' not found.");

        try
        {
            entry.Update(cmd.Date, cmd.Category, cmd.TaskId, cmd.ProjectId, cmd.Hours, cmd.Note);
        }
        catch (DomainException ex)
        {
            return ServiceResult<TimeEntryDto>.Fail("BUSINESS_RULE_VIOLATION", ex.Message);
        }

        await _timeEntries.SaveChangesAsync(ct);

        await _audit.LogAsync("TIME_ENTRY_UPDATED", cmd.ActorId, cmd.IpAddress,
            $"Engineer {cmd.ActorId} updated time entry {entry.Id}", ct);

        return ServiceResult<TimeEntryDto>.Ok(TimeEntryDto.From(entry));
    }
}
