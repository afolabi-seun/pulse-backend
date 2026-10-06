using System.Security.Cryptography;
using System.Text;
using Pulse.Application.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Pulse.Api.Attributes;

/// <summary>
/// Gates operator-only endpoints (creating organizations) behind a shared secret, <c>OPERATOR_API_KEY</c>,
/// sent as the <c>X-Operator-Key</c> header. Mirrors the demo-endpoint gate: when the key isn't configured
/// the endpoints answer 404, so they don't exist on a server nobody deliberately enabled them on. The
/// comparison is constant-time. No user identity is involved, so the request runs with no organization —
/// operator handlers see every org, which is what they're for.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class OperatorAuthAttribute : Attribute, IAuthorizationFilter
{
    public const string HeaderName = "X-Operator-Key";
    public const string SettingName = "OPERATOR_API_KEY";
    private const int MinimumKeyLength = 32;

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var config = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var configured = config[SettingName];
        if (string.IsNullOrEmpty(configured) || configured.Length < MinimumKeyLength)
        {
            context.Result = new NotFoundResult();
            return;
        }

        var supplied = context.HttpContext.Request.Headers[HeaderName].ToString();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(configured)))
        {
            context.Result = new UnauthorizedObjectResult(
                ApiResponse<object>.Failure("UNAUTHORIZED", "A valid operator key is required."));
        }
    }
}
