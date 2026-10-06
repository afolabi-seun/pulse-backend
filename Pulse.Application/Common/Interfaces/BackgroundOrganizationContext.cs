namespace Pulse.Application.Common.Interfaces;

/// <summary>
/// The organization background work is acting for, when there's no HTTP caller to take it from. Scoped:
/// OrganizationJobRunner sets it on a fresh scope before resolving the job, and CurrentUserService falls
/// back to it, so the EF org filters, the RLS org stamp, new-row stamping and per-org thresholds all follow.
/// Null (the default) keeps today's unscoped behavior for ad-hoc jobs that aren't run per organization.
/// </summary>
public sealed class BackgroundOrganizationContext
{
    public Guid? OrganizationId { get; set; }
}
