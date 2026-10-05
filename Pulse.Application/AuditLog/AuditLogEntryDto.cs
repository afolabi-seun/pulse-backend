namespace Pulse.Application.AuditLog;

public record AuditLogEntryDto(
    long Id,
    string Action,
    Guid ActorId,
    string? IpAddress,
    string? Detail,
    DateTime Ts);

public record AuditLogPageDto(
    IReadOnlyList<AuditLogEntryDto> Items,
    long? NextCursor,
    bool HasMore);
