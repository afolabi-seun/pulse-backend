using System.Collections.Concurrent;

namespace Pulse.Application.Overwork;

/// <summary>
/// Process-wide cache of each organization's <see cref="OverworkThresholds"/>, replacing the single
/// app-wide instance that predated multi-tenancy (one org's admin changing thresholds would otherwise
/// change them for every org). Entries are loaded on first use and updated in place by
/// UpdateThresholdsHandler, exactly as the single instance used to be.
/// </summary>
public class OrganizationThresholdsCache
{
    private readonly ConcurrentDictionary<Guid, OverworkThresholds> _byOrganization = new();

    public bool TryGet(Guid organizationId, out OverworkThresholds thresholds) =>
        _byOrganization.TryGetValue(organizationId, out thresholds!);

    /// <summary>Caches <paramref name="loaded"/> unless another request cached this org first, and
    /// returns whichever instance won — so every caller shares one instance per org.</summary>
    public OverworkThresholds Add(Guid organizationId, OverworkThresholds loaded) =>
        _byOrganization.GetOrAdd(organizationId, loaded);
}
