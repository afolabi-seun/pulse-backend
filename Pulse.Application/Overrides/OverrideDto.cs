namespace Pulse.Application.Overrides;

public record OverrideDto(
    Guid Id,
    Guid EngineerId,
    string Reason,
    DateTime ExpiresAt,
    Guid GrantedById,
    bool IsActive,
    DateTime CreatedAt)
{
    public static OverrideDto From(Domain.Overrides.OverworkOverride o) =>
        new(o.Id, o.EngineerId, o.Reason, o.ExpiresAt, o.GrantedById, o.IsActive, o.CreatedAt);
}
