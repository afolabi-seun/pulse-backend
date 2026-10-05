using Pulse.Application.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Pulse.Api.Attributes;

/// <summary>
/// Validates service-to-service JWT tokens. Checks for a non-empty <c>serviceId</c> claim,
/// which is only present in tokens issued by <see cref="Application.Common.Interfaces.IJwtService.GenerateServiceToken"/>.
/// Internal endpoints decorated with this attribute are hidden from the public Swagger UI.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class ServiceAuthAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var user = context.HttpContext.User;

        if (!user.Identity?.IsAuthenticated ?? true)
        {
            context.Result = new UnauthorizedObjectResult(
                ApiResponse<object>.Failure("UNAUTHORIZED", "Authentication required."));
            return;
        }

        var serviceId = user.FindFirst("serviceId")?.Value;
        if (string.IsNullOrWhiteSpace(serviceId))
        {
            context.Result = new ObjectResult(
                ApiResponse<object>.Failure("SERVICE_NOT_AUTHORIZED", "Service authentication required."))
            { StatusCode = StatusCodes.Status403Forbidden };
        }
    }
}
