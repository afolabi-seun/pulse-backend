using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Reports;
using Pulse.Application.TimeEntries;
using Pulse.Application.TimeEntries.Queries;
using Pulse.Domain.Engineers;
using Pulse.Domain.TimeEntries;
using Pulse.Domain.Organizations;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Reports;

/// <summary>The PMO can start reports from a given day. Older records are kept and come back when the date is removed.</summary>
[Collection("Integration")]
public class ReportingBaselineTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public ReportingBaselineTests(PulseWebApplicationFactory factory) : base(factory) { }

    private async Task LogDirectAsync(Guid engineerId, DateOnly date, decimal hours)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        db.TimeEntries.Add(TimeEntry.Log(engineerId, date, TimeEntryCategory.Meeting, null, null, hours, null));
        await db.SaveChangesAsync();
    }

    private async Task<decimal> HoursAsync(HttpClient client, Guid engineerId, DateOnly from, DateOnly to)
    {
        var resp = await client.GetAsync($"/api/v1/time-entries/engineer-activity?engineerId={engineerId}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await resp.Content.ReadFromJsonAsync<ApiResponse<EngineerTimeActivityDto>>(JsonOpts))!.Data!;
        return dto.OtherTime.Sum(c => c.Hours);
    }

    private static HttpContent Date(DateOnly? d) => JsonContent.Create(new { date = d?.ToString("yyyy-MM-dd") });

    [Fact]
    public async Task Hours_before_the_baseline_drop_out_of_reports_and_return_when_it_is_removed()
    {
        var engineer = await SeedEngineerAsync("bl_eng@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("bl_pmo@pulse.io", Roles.HeadOfPmo);
        await SeedEngineerAsync("bl_hr@pulse.io", Roles.HR);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var older = today.AddDays(-10);
        await LogDirectAsync(engineer.Id, older, 3m);   // before the baseline
        await LogDirectAsync(engineer.Id, today, 2m);   // after it

        var pmo = await AuthenticatedClientAsync("bl_pmo@pulse.io");
        var hr = await AuthenticatedClientAsync("bl_hr@pulse.io");

        (await HoursAsync(hr, engineer.Id, older, today)).Should().Be(5m, "with no baseline everything in the range counts");

        (await pmo.PutAsync("/api/v1/reports/baseline", Date(today.AddDays(-5)))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await HoursAsync(hr, engineer.Id, older, today)).Should().Be(2m, "the older day is before the baseline");

        (await pmo.PutAsync("/api/v1/reports/baseline", Date(null))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await HoursAsync(hr, engineer.Id, older, today)).Should().Be(5m, "nothing was deleted, so removing the date brings it all back");
    }

    [Fact]
    public async Task Everyone_can_read_the_baseline_but_only_the_PMO_can_change_it()
    {
        await SeedEngineerAsync("bl2_eng@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("bl2_pmo@pulse.io", Roles.HeadOfPmo);
        var eng = await AuthenticatedClientAsync("bl2_eng@pulse.io");
        var pmo = await AuthenticatedClientAsync("bl2_pmo@pulse.io");
        var day = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3);

        (await eng.PutAsync("/api/v1/reports/baseline", Date(day))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await pmo.PutAsync("/api/v1/reports/baseline", Date(day))).StatusCode.Should().Be(HttpStatusCode.OK);

        var read = await (await eng.GetAsync("/api/v1/reports/baseline")).Content.ReadFromJsonAsync<ApiResponse<ReportingBaselineDto>>(JsonOpts);
        read!.Data!.Date.Should().Be(day);

        await pmo.PutAsync("/api/v1/reports/baseline", Date(null)); // leave the shared server clean for other tests
    }

    [Fact]
    public async Task A_baseline_in_the_future_is_refused()
    {
        await SeedEngineerAsync("bl3_pmo@pulse.io", Roles.HeadOfPmo);
        var pmo = await AuthenticatedClientAsync("bl3_pmo@pulse.io");

        var response = await pmo.PutAsync("/api/v1/reports/baseline", Date(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30)));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task One_organizations_baseline_does_not_apply_to_another()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        Guid orgB;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var org = Organization.Create("Baseline org B", $"baseline-b-{suffix}");
            db.Organizations.Add(org);
            await db.SaveChangesAsync();
            orgB = org.Id;
        }

        await SeedEngineerAsync($"blorg_a_{suffix}@pulse.io", Roles.HeadOfPmo);
        var pmoB = await SeedEngineerAsync($"blorg_b_{suffix}@pulse.io", Roles.HeadOfPmo);
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            db.Attach(pmoB);
            db.Entry(pmoB).Property("OrganizationId").CurrentValue = orgB;
            await db.SaveChangesAsync();
        }
        var day = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-4);
        var a = await AuthenticatedClientAsync($"blorg_a_{suffix}@pulse.io");
        var b = await AuthenticatedClientAsync($"blorg_b_{suffix}@pulse.io");

        (await a.PutAsync("/api/v1/reports/baseline", Date(day))).StatusCode.Should().Be(HttpStatusCode.OK);

        var readB = await (await b.GetAsync("/api/v1/reports/baseline")).Content.ReadFromJsonAsync<ApiResponse<ReportingBaselineDto>>(JsonOpts);
        readB!.Data!.Date.Should().BeNull("organization B never set one");
        var readA = await (await a.GetAsync("/api/v1/reports/baseline")).Content.ReadFromJsonAsync<ApiResponse<ReportingBaselineDto>>(JsonOpts);
        readA!.Data!.Date.Should().Be(day);

        await a.PutAsync("/api/v1/reports/baseline", Date(null)); // leave the shared server clean for other tests
    }
}
