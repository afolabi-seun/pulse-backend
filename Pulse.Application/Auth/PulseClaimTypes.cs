namespace Pulse.Application.Auth;

/// <summary>Pulse's own JWT claim names — shared by JwtService (which writes them) and
/// CurrentUserService (which reads them), so the two can't drift apart.</summary>
public static class PulseClaimTypes
{
    /// <summary>The caller's Organization id. Absent on access tokens issued before multi-tenancy
    /// Phase 1a; those pick it up at their next refresh (RefreshTokenCommand rebuilds claims).</summary>
    public const string OrganizationId = "org_id";
}
