using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.Persistence.Repositories;

public class ThresholdRepository : IThresholdRepository
{
    private readonly PulseDbContext _db;

    public ThresholdRepository(PulseDbContext db) => _db = db;

    // Settings are keyed per organization. Until multi-tenancy Phase 1c passes the caller's org in,
    // every read and write is explicitly the default org's — the only org that exists.
    private static readonly Guid OrganizationId = Organization.DefaultId;

    public async Task<Dictionary<string, string>> LoadAllAsync(CancellationToken ct = default) =>
        await _db.ThresholdSettings
            .Where(t => t.OrganizationId == OrganizationId)
            .ToDictionaryAsync(t => t.Key, t => t.Value, ct);

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        var setting = await _db.ThresholdSettings.FindAsync([OrganizationId, key], ct);
        if (setting is null)
            await _db.ThresholdSettings.AddAsync(new ThresholdSetting { OrganizationId = OrganizationId, Key = key, Value = value }, ct);
        else
            setting.Value = value;

        await _db.SaveChangesAsync(ct);
    }
}
