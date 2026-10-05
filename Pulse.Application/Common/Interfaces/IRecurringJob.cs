namespace Pulse.Application.Common.Interfaces;

/// <summary>
/// A scheduled background job that runs once per organization: the Hangfire registration goes through
/// OrganizationJobRunner, which resolves the job in a fresh scope per organization with that organization
/// as the acting one (see <see cref="BackgroundOrganizationContext"/>). The job itself is written as if
/// there were only one organization — its queries, RLS stamp and thresholds are already scoped.
/// </summary>
public interface IRecurringJob
{
    Task RunAsync(CancellationToken ct = default);
}
