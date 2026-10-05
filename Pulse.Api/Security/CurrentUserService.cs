using System.Security.Claims;
using Pulse.Application.Auth;
using Pulse.Application.Common.Interfaces;

namespace Pulse.Api.Security;

/// <summary>Reads the caller's identity from the ambient HTTP request's claims.</summary>
public class CurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor _http;

    public CurrentUserService(IHttpContextAccessor http) => _http = http;

    private ClaimsPrincipal? User =>
        _http.HttpContext?.User is { Identity.IsAuthenticated: true } user ? user : null;

    public bool IsAuthenticated => User is not null;

    public Guid? UserId => ParseGuid(User?.FindFirstValue(ClaimTypes.NameIdentifier));

    public string? Role => User?.FindFirstValue(ClaimTypes.Role);

    public Guid? OrganizationId => ParseGuid(User?.FindFirstValue(PulseClaimTypes.OrganizationId));

    private static Guid? ParseGuid(string? value) => Guid.TryParse(value, out var id) ? id : null;
}
