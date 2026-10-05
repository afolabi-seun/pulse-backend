namespace Pulse.Infrastructure.Persistence;

public class AuditLogEntry
{
    public long Id { get; set; }
    public string Action { get; set; } = string.Empty;
    public Guid ActorId { get; set; }
    public string? IpAddress { get; set; }
    public string? Detail { get; set; }
    public DateTime Ts { get; set; } = DateTime.UtcNow;
}
