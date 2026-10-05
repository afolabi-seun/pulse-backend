using Pulse.Application.Auth;
using Pulse.Application.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Pulse.Api.Authorization;

/// <summary>
/// Requires the caller's role to satisfy the named capability, looked up from
/// <see cref="CapabilityRegistry.All"/>. Replaces Pulse's former per-attribute role-set
/// classes (see docs/rbac-consolidation.md) with one generic filter, e.g.
/// <c>[RequiresCapability(CapabilityRegistry.ProductManagerOrAbove)]</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class RequiresCapabilityAttribute : Attribute, IAuthorizationFilter
{
    private readonly string[] _capabilityKeys;

    /// <summary>Access is granted if the caller's role satisfies ANY one of the given capabilities.</summary>
    public RequiresCapabilityAttribute(params string[] capabilityKeys) => _capabilityKeys = capabilityKeys;

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var capabilities = _capabilityKeys.Select(key =>
            CapabilityRegistry.All.TryGetValue(key, out var capability)
                ? capability
                : throw new InvalidOperationException($"Unknown capability key '{key}'.")
        ).ToList();

        var user = context.HttpContext.User;

        if (!user.Identity?.IsAuthenticated ?? true)
        {
            context.Result = new UnauthorizedObjectResult(
                ApiResponse<object>.Failure("UNAUTHORIZED", "Authentication required."));
            return;
        }

        var role = user.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value;
        if (role is null || !capabilities.Any(c => c.AllowedRoles.Contains(role)))
        {
            context.Result = new ObjectResult(
                ApiResponse<object>.Failure("FORBIDDEN", capabilities[0].ForbiddenMessage))
            { StatusCode = StatusCodes.Status403Forbidden };
        }
    }
}
