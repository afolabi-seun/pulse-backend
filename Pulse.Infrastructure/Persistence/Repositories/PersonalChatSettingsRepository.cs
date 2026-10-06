using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.Persistence.Repositories;

public class PersonalChatSettingsRepository : IPersonalChatSettingsRepository
{
    private readonly PulseDbContext _db;

    public PersonalChatSettingsRepository(PulseDbContext db) => _db = db;

    public Task<PersonalChatSettings?> GetAsync(Guid engineerId, CancellationToken ct = default) =>
        _db.PersonalChatSettings.FirstOrDefaultAsync(s => s.EngineerId == engineerId, ct);

    public async Task<PersonalChatSettings> GetOrCreateAsync(Guid engineerId, CancellationToken ct = default)
    {
        var existing = await GetAsync(engineerId, ct);
        if (existing is not null)
            return existing;
        var created = PersonalChatSettings.For(engineerId);
        await _db.PersonalChatSettings.AddAsync(created, ct);
        return created;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}
