using Pulse.Domain.Alerts;

namespace Pulse.Application.Common.Interfaces;

public interface IGoogleChatSpaceRepository
{
    Task<IReadOnlyList<GoogleChatSpace>> ListAllAsync(CancellationToken ct = default);
    Task<GoogleChatSpace?> GetBySpaceIdAsync(string spaceId, CancellationToken ct = default);

    /// <summary>Creates or refreshes the stored space — the receiving controller calls this on
    /// every ADDED_TO_SPACE-shaped event, since a space's display name can legitimately change.</summary>
    Task UpsertAsync(string spaceId, string displayName, CancellationToken ct = default);

    Task SaveChangesAsync(CancellationToken ct = default);
}
