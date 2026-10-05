using Pulse.Api.Attributes;
using Microsoft.AspNetCore.SignalR;

namespace Pulse.Api.Hubs;

/// <summary>
/// Real-time hub for server-push events. Clients connect once on login and stay connected.
/// The server pushes to Clients.User(userId) — no client-invocable methods needed for V1.
/// </summary>
[EngineerAuth]
public class PulseHub : Hub
{
    // No client-invocable methods in V1 — the hub is send-only from server side.
    // Future: subscribe/unsubscribe to task groups for board-level updates.
}
