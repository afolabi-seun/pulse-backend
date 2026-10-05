using Pulse.Application.Common.Interfaces;
using Pulse.Application.Overwork;
using Pulse.Domain.Organizations;
using Microsoft.Extensions.Logging;

namespace Pulse.Infrastructure.Persistence;

/// <summary>
/// Resolves the current organization's <see cref="OverworkThresholds"/> — the caller's org, or the
/// default org for background work until multi-tenancy Phase 1e runs jobs per org. Registered as the
/// scoped factory for OverworkThresholds, so its consumers keep taking OverworkThresholds directly.
/// </summary>
public class OrganizationThresholdsProvider
{
    private readonly OrganizationThresholdsCache _cache;
    private readonly ICurrentUserService _currentUser;
    private readonly PulseDbContext _db;
    private readonly ILogger<OrganizationThresholdsProvider> _logger;

    public OrganizationThresholdsProvider(OrganizationThresholdsCache cache, ICurrentUserService currentUser,
        PulseDbContext db, ILogger<OrganizationThresholdsProvider> logger)
    {
        _cache = cache;
        _currentUser = currentUser;
        _db = db;
        _logger = logger;
    }

    public OverworkThresholds ForCurrentOrganization()
    {
        var orgId = _currentUser.OrganizationId ?? Organization.DefaultId;
        if (_cache.TryGet(orgId, out var cached))
            return cached;

        var thresholds = new OverworkThresholds();
        try
        {
            // Synchronous: DI factories can't await. Runs once per org per process.
            var settings = _db.ThresholdSettings
                .Where(t => t.OrganizationId == orgId)
                .ToDictionary(t => t.Key, t => t.Value);
            thresholds.Apply(settings);
        }
        catch (Exception ex)
        {
            // Non-critical, as the old startup hydration was: defaults exist. Not cached, so the next
            // request retries the load instead of pinning this org to defaults for the process lifetime.
            _logger.LogWarning(ex, "Failed to load overwork thresholds for organization {OrganizationId}; using defaults.", orgId);
            return thresholds;
        }

        return _cache.Add(orgId, thresholds);
    }
}
