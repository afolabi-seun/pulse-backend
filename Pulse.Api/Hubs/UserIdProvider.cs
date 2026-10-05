using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;

namespace Pulse.Api.Hubs;

/// <summary>Maps the JWT NameIdentifier claim to the SignalR user ID so Clients.User() targets the right connections.</summary>
public class PulseUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) =>
        connection.User.FindFirstValue(ClaimTypes.NameIdentifier);
}
