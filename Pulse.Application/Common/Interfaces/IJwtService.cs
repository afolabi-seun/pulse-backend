using Pulse.Domain.Engineers;

namespace Pulse.Application.Common.Interfaces;

public interface IJwtService
{
    string GenerateAccessToken(Engineer engineer);
    string GenerateRefreshToken();
    string HashToken(string token);
    /// <summary>
    /// Issues a service JWT carrying a <c>serviceId</c> claim for inter-service calls.
    /// Tokens are valid for 24 hours. The <c>serviceId</c> claim is what
    /// <see cref="Pulse.Api.Attributes.ServiceAuthAttribute"/> checks to identify service callers.
    /// </summary>
    string GenerateServiceToken(string serviceId);
}
