namespace Pulse.Application.Common.Interfaces;

/// <summary>
/// The authenticated caller, read once from the current request's claims. The single place
/// org-aware code gets the caller's identity from — rather than the per-controller claim helpers
/// (e.g. TasksController.GetActorId), which only know user id and role. Everything is null when
/// there's no authenticated request (background jobs, anonymous endpoints).
/// </summary>
public interface ICurrentUserService
{
    /// <summary>True for an authenticated <em>user</em>. A service-to-service token is not one — see
    /// <see cref="IsServiceCaller"/>.</summary>
    bool IsAuthenticated { get; }
    /// <summary>The request carries a service-to-service token: org-agnostic, and RLS's <c>service</c> role,
    /// as the multi-tenancy design specifies for service calls.</summary>
    bool IsServiceCaller { get; }
    Guid? UserId { get; }
    string? Role { get; }
    /// <summary>The authenticated caller's organization (null for a token without the org_id claim), or —
    /// with no authenticated caller — the organization background work is running for, if any.</summary>
    Guid? OrganizationId { get; }
}
