using Pulse.Domain.Organizations;

namespace Pulse.Application.Common.Interfaces;

public interface IOrganizationRepository
{
    Task<Organization?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<bool> SlugExistsAsync(string slug, CancellationToken ct = default);
    /// <summary>Every organization with its engineer count, oldest first. Operator use only — callers
    /// with an organization would only ever see their own.</summary>
    Task<IReadOnlyList<(Organization Organization, int EngineerCount)>> ListWithEngineerCountsAsync(CancellationToken ct = default);
    Task AddAsync(Organization organization, CancellationToken ct = default);
}
