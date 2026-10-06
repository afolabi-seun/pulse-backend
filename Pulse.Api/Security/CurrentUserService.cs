using System.Security.Claims;
using Pulse.Application.Auth;
using Pulse.Application.Common.Interfaces;

namespace Pulse.Api.Security;

/// <summary>Reads the caller's identity from the ambient HTTP request's claims.</summary>
public class CurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor _http;
    private readonly BackgroundOrganizationContext? _background;

    public CurrentUserService(IHttpContextAccessor http, BackgroundOrganizationContext? background = null)
    {
        _http = http;
        _background = background;
    }

    // A service-to-service token (IJwtService.GenerateServiceToken) authenticates a service, not a user:
    // it carries a serviceId and no user, role or organization. It must not count as an authenticated
    // user, or the org filters would treat it as "a user with no org" and show it nothing.
    private ClaimsPrincipal? User =>
        _http.HttpContext?.User is { Identity.IsAuthenticated: true } user && !IsServiceToken(user) ? user : null;

    public bool IsAuthenticated => User is not null;

    public bool IsServiceCaller =>
        _http.HttpContext?.User is { Identity.IsAuthenticated: true } user && IsServiceToken(user);

    private static bool IsServiceToken(ClaimsPrincipal user) =>
        !string.IsNullOrWhiteSpace(user.FindFirstValue("serviceId"));

    public Guid? UserId => ParseGuid(User?.FindFirstValue(ClaimTypes.NameIdentifier));

    public string? Role => User?.FindFirstValue(ClaimTypes.Role);

    /// <summary>The authenticated caller's org; with no authenticated caller, the organization background
    /// work is running for (OrganizationJobRunner), if any.</summary>
    public Guid? OrganizationId => User is not null
        ? ParseGuid(User.FindFirstValue(PulseClaimTypes.OrganizationId))
        : _background?.OrganizationId;

    private static Guid? ParseGuid(string? value) => Guid.TryParse(value, out var id) ? id : null;
}
