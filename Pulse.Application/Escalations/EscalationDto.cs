namespace Pulse.Application.Escalations;

public record EscalationDto(
    Guid TaskId,
    string TaskTitle,
    Guid? AssigneeId,
    DateOnly? DueDate,
    string Level,
    int DaysUntilDue,
    string? ProjectName = null,
    string? TaskKey = null);
