using Asp.Versioning;
using Pulse.Api.Security;
using Pulse.Application.Common.Interfaces;
using Pulse.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Pulse.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/demo")]
public class DemoController(IDemoSeeder seeder, IHostEnvironment env, IConfiguration config, PulseDbContext db) : ControllerBase
{
    /// <summary>Reset and clear wipe every row in the database — all organizations' data, not one tenant's. A database that holds
    /// more than the single default organization has real customers in it, so these refuse outright, whatever the environment says.</summary>
    private async Task<bool> HoldsOtherTenantsAsync(CancellationToken ct) => await db.Organizations.CountAsync(ct) > 1;

    private IActionResult OtherTenantsRefusal() => Conflict(new
    {
        message = "This database holds more than one organization. Demo reset and clear wipe every organization's data, so they are refused.",
    });

    /// <summary>These endpoints wipe the whole database and take no login, so the environment name alone must not be what stands
    /// between the internet and the data: a server that is wrongly left in Development would otherwise expose them. They also need
    /// ENABLE_DEMO_ENDPOINTS=true, which a local developer sets deliberately (launchSettings does) and no server should.</summary>
    private bool Enabled => env.IsDevelopment() && config.GetValue<bool>("ENABLE_DEMO_ENDPOINTS");

    /// <summary>
    /// Wipes all data and loads fresh demo seed data.
    /// Only available when ASPNETCORE_ENVIRONMENT=Development and ENABLE_DEMO_ENDPOINTS=true.
    /// </summary>
    /// <remarks>
    /// This endpoint carries no authenticated user, so it opts into the <c>service</c> RLS role
    /// explicitly (see RlsServiceOverride) — otherwise its writes would silently no-op under a
    /// genuinely RLS-enforced connection, the same class of bug as the 2026-09-05 migration
    /// incident. Currently masked in local dev only because that connection is a superuser.
    /// </remarks>
    [HttpPost("reset")]
    public async Task<IActionResult> Reset(CancellationToken ct)
    {
        if (!Enabled) return NotFound();
        RlsServiceOverride.Apply(HttpContext);
        if (await HoldsOtherTenantsAsync(ct)) return OtherTenantsRefusal();
        var summary = await seeder.SeedAsync(ct);
        return Ok(summary);
    }

    /// <summary>
    /// Wipes all data and leaves the database empty.
    /// Only available when ASPNETCORE_ENVIRONMENT=Development and ENABLE_DEMO_ENDPOINTS=true.
    /// </summary>
    /// <remarks>Opts into the service RLS role — see the remarks on <see cref="Reset"/>.</remarks>
    [HttpPost("clear")]
    public async Task<IActionResult> Clear(CancellationToken ct)
    {
        if (!Enabled) return NotFound();
        RlsServiceOverride.Apply(HttpContext);
        if (await HoldsOtherTenantsAsync(ct)) return OtherTenantsRefusal();
        await seeder.ClearAsync(ct);
        return Ok(new { message = "All data cleared." });
    }
}
