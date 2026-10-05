using Hangfire;
using Pulse.Application.Common.Interfaces;
using Pulse.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Pulse.Infrastructure.BackgroundJobs;

/// <summary>
/// Runs a recurring job once per active organization (multi-tenancy Phase 1e).
///
/// The recurring schedule calls <see cref="RunForAllOrganizationsAsync"/>, which only fans out: it
/// enqueues one <see cref="RunForOrganizationAsync"/> child per organization. Each child is its own
/// Hangfire job, so a failure is retried for that organization alone — retrying a single job that looped
/// over every org would re-send reminders and digests to the orgs that had already succeeded.
///
/// A child resolves the job in a fresh DI scope whose <see cref="BackgroundOrganizationContext"/> is that
/// organization, so everything the job touches follows: the EF org filters, the RLS org stamp, new-row
/// stamping and per-org thresholds. Jobs themselves stay written as if there were one organization.
///
/// Generic on the class, not the methods: Hangfire restores a stored job by looking its method up by
/// signature, which fails for generic methods, but a closed generic type round-trips by name.
/// </summary>
public class OrganizationJobRunner<TJob> where TJob : IRecurringJob
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IBackgroundJobClient _jobs;
    private readonly ILogger<OrganizationJobRunner<TJob>> _logger;

    public OrganizationJobRunner(IServiceScopeFactory scopes, IBackgroundJobClient jobs, ILogger<OrganizationJobRunner<TJob>> logger)
    {
        _scopes = scopes;
        _jobs = jobs;
        _logger = logger;
    }

    /// <summary>The fan-out itself does nothing an immediate retry would fix differently, and retrying it
    /// after a partial enqueue would enqueue some organizations twice — so it doesn't retry.</summary>
    [AutomaticRetry(Attempts = 0)]
    public async Task RunForAllOrganizationsAsync(CancellationToken ct)
    {
        List<Guid> organizationIds;
        using (var scope = _scopes.CreateScope())
        {
            // No organization on this scope: unfiltered, and RLS sees every org (service, no org stamped).
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            organizationIds = await db.Organizations
                .Where(o => o.IsActive)
                .OrderBy(o => o.CreatedAt)
                .Select(o => o.Id)
                .ToListAsync(ct);
        }

        foreach (var organizationId in organizationIds)
            _jobs.Enqueue<OrganizationJobRunner<TJob>>(r => r.RunForOrganizationAsync(organizationId, CancellationToken.None));

        _logger.LogInformation("Queued {Job} for {Count} organization(s).", typeof(TJob).Name, organizationIds.Count);
    }

    public async Task RunForOrganizationAsync(Guid organizationId, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        // Set before anything org-aware (DbContext, thresholds) is resolved from this scope.
        scope.ServiceProvider.GetRequiredService<BackgroundOrganizationContext>().OrganizationId = organizationId;

        using var _ = _logger.BeginScope(new Dictionary<string, object> { ["OrganizationId"] = organizationId });
        var job = ActivatorUtilities.GetServiceOrCreateInstance<TJob>(scope.ServiceProvider);
        await job.RunAsync(ct);
    }
}
