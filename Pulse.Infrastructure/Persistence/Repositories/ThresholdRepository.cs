using Pulse.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.Persistence.Repositories;

public class ThresholdRepository : IThresholdRepository
{
    private readonly PulseDbContext _db;

    public ThresholdRepository(PulseDbContext db) => _db = db;

    public async Task<Dictionary<string, string>> LoadAllAsync(CancellationToken ct = default) =>
        await _db.ThresholdSettings.ToDictionaryAsync(t => t.Key, t => t.Value, ct);

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        var setting = await _db.ThresholdSettings.FindAsync([key], ct);
        if (setting is null)
            await _db.ThresholdSettings.AddAsync(new ThresholdSetting { Key = key, Value = value }, ct);
        else
            setting.Value = value;

        await _db.SaveChangesAsync(ct);
    }
}
