using Pulse.Application.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Pulse.Api.Attributes;

/// <summary>
/// Requires any authenticated user (engineer, team lead, PM, or Head of R&amp;D).
/// Use this as the minimum guard on endpoints that any staff member can reach.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class EngineerAuthAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (!context.HttpContext.User.Identity?.IsAuthenticated ?? true)
        {
            context.Result = new UnauthorizedObjectResult(
                ApiResponse<object>.Failure("UNAUTHORIZED", "Authentication required."));
        }
    }
}
