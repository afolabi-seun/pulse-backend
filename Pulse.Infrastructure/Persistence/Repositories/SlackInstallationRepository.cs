using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Integrations;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.Persistence.Repositories;

public class SlackInstallationRepository : ISlackInstallationRepository
{
    private readonly PulseDbContext _db;

    public SlackInstallationRepository(PulseDbContext db) => _db = db;

    public Task<SlackInstallation?> GetByOrganizationAsync(Guid organizationId, CancellationToken ct = default) =>
        _db.SlackInstallations.FirstOrDefaultAsync(s => s.OrganizationId == organizationId, ct);

    public Task<SlackInstallation?> GetByTeamIdAsync(string teamId, CancellationToken ct = default) =>
        _db.SlackInstallations.FirstOrDefaultAsync(s => s.TeamId == teamId, ct);

    public async Task AddAsync(SlackInstallation installation, CancellationToken ct = default) =>
        await _db.SlackInstallations.AddAsync(installation, ct);

    public void Remove(SlackInstallation installation) => _db.SlackInstallations.Remove(installation);

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}
