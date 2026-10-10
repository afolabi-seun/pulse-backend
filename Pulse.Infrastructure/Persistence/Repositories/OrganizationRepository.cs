using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Infrastructure.Persistence.Repositories;

public class OrganizationRepository : IOrganizationRepository
{
    private readonly PulseDbContext _db;

    public OrganizationRepository(PulseDbContext db) => _db = db;

    public Task<Organization?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _db.Organizations.FirstOrDefaultAsync(o => o.Id == id, ct);

    public Task<bool> SlugExistsAsync(string slug, CancellationToken ct = default) =>
        _db.Organizations.AnyAsync(o => o.Slug == slug, ct);

    public async Task<IReadOnlyList<(Organization Organization, int EngineerCount)>> ListWithEngineerCountsAsync(CancellationToken ct = default)
    {
        var rows = await _db.Organizations
            .OrderBy(o => o.CreatedAt)
            .Select(o => new { Organization = o, EngineerCount = _db.Engineers.Count(e => e.OrganizationId == o.Id) })
            .ToListAsync(ct);
        return rows.Select(r => (r.Organization, r.EngineerCount)).ToList();
    }

    public async Task AddAsync(Organization organization, CancellationToken ct = default) =>
        await _db.Organizations.AddAsync(organization, ct);

    public Task<OrganizationLogo?> GetLogoAsync(Guid organizationId, CancellationToken ct = default) =>
        _db.OrganizationLogos.FirstOrDefaultAsync(l => l.OrganizationId == organizationId, ct);

    public async Task AddLogoAsync(OrganizationLogo logo, CancellationToken ct = default) =>
        await _db.OrganizationLogos.AddAsync(logo, ct);

    public void RemoveLogo(OrganizationLogo logo) => _db.OrganizationLogos.Remove(logo);

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}
