namespace Pulse.Application.Common.Interfaces;

/// <summary>
/// The authenticated caller, read once from the current request's claims. The single place
/// org-aware code gets the caller's identity from — rather than the per-controller claim helpers
/// (e.g. TasksController.GetActorId), which only know user id and role. Everything is null when
/// there's no authenticated request (background jobs, anonymous endpoints).
/// </summary>
public interface ICurrentUserService
{
    bool IsAuthenticated { get; }
    Guid? UserId { get; }
    string? Role { get; }
    /// <summary>Null for tokens issued before the org_id claim existed, until their next refresh.</summary>
    Guid? OrganizationId { get; }
}
