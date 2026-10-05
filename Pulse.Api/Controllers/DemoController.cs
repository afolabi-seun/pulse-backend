using Asp.Versioning;
using Pulse.Api.Security;
using Pulse.Application.Common.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace Pulse.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/demo")]
public class DemoController(IDemoSeeder seeder, IHostEnvironment env) : ControllerBase
{
    /// <summary>
    /// Wipes all data and loads fresh demo seed data.
    /// Only available when ASPNETCORE_ENVIRONMENT=Development.
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
        if (!env.IsDevelopment()) return NotFound();
        RlsServiceOverride.Apply(HttpContext);
        var summary = await seeder.SeedAsync(ct);
        return Ok(summary);
    }

    /// <summary>
    /// Wipes all data and leaves the database empty.
    /// Only available when ASPNETCORE_ENVIRONMENT=Development.
    /// </summary>
    /// <remarks>Opts into the service RLS role — see the remarks on <see cref="Reset"/>.</remarks>
    [HttpPost("clear")]
    public async Task<IActionResult> Clear(CancellationToken ct)
    {
        if (!env.IsDevelopment()) return NotFound();
        RlsServiceOverride.Apply(HttpContext);
        await seeder.ClearAsync(ct);
        return Ok(new { message = "All data cleared." });
    }
}
