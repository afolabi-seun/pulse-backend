using Pulse.Domain.Common;

namespace Pulse.Domain.Overrides;

public class OverworkOverride : Entity
{
    public Guid EngineerId { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public DateTime ExpiresAt { get; private set; }
    public Guid GrantedById { get; private set; }

    private OverworkOverride() { }

    public static OverworkOverride Grant(Guid engineerId, string reason, DateTime expiresAt, Guid grantedById) =>
        new() { EngineerId = engineerId, Reason = reason, ExpiresAt = expiresAt, GrantedById = grantedById };

    public bool IsActive => DateTime.UtcNow < ExpiresAt;
}
