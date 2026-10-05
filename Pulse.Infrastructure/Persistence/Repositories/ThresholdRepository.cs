using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.Persistence.Repositories;

public class ThresholdRepository : IThresholdRepository
{
    private readonly PulseDbContext _db;
    private readonly ICurrentUserService _currentUser;

    public ThresholdRepository(PulseDbContext db, ICurrentUserService currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    // Settings are keyed per organization: the caller's. Background jobs have no caller and use the
    // default org's settings until multi-tenancy Phase 1e runs them per organization.
    private Guid OrganizationId => _currentUser.OrganizationId ?? Organization.DefaultId;

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
