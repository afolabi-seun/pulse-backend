namespace Pulse.Application.Auth;

/// <summary>Returned on successful login or token refresh.</summary>
public record AuthDto(string AccessToken, string RefreshToken, AuthUserDto User);

/// <summary>
/// Subset of engineer data embedded in auth responses. <c>Capabilities</c> is the caller's fully
/// resolved <see cref="CapabilityRegistry"/> key set — the frontend should gate role-level UI
/// (nav items, buttons, forms) on this rather than re-deriving role-to-capability logic locally;
/// see docs/rbac-consolidation.md. <c>Permissions</c> predates it (a narrower, separately-defined
/// code list) and is kept only until the frontend migration in a later phase removes it.
/// </summary>
public record AuthUserDto(
    Guid Id, string Name, string Email, string Role,
    IReadOnlyList<string> Permissions, IReadOnlyList<string> Capabilities);
