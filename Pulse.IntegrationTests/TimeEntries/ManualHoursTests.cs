using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.TimeEntries;
using Pulse.Domain.Engineers;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace Pulse.IntegrationTests.TimeEntries;

/// <summary>Time typed in by hand (the My Time grid, the add-a-session box, or any API client) is kept to two decimals: the entry has the same value in
/// the response, in the list and after a reload, and a sliver that would round to zero is refused rather than stored as a zero-hour row.</summary>
[Collection("Integration")]
public class ManualHoursTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public ManualHoursTests(PulseWebApplicationFactory factory) : base(factory) { }

    private static readonly string Today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");

    private async Task<HttpClient> ClientAsync(string tag)
    {
        await SeedEngineerAsync($"mh_{tag}@pulse.io", Roles.Engineer);
        return await AuthenticatedClientAsync($"mh_{tag}@pulse.io");
    }

    private static async Task<TimeEntryDto> Read(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<ApiResponse<TimeEntryDto>>(JsonOpts))!.Data!;

    [Theory]
    [InlineData(3.4200000000000004, 3.42)]
    [InlineData(1.005, 1.01)]
    [InlineData(0.1, 0.1)]
    public async Task Hours_typed_with_stray_decimals_are_stored_and_returned_to_two_places(double sent, double expected)
    {
        var client = await ClientAsync($"log_{sent}");

        var created = await client.PostAsJsonAsync("/api/v1/time-entries", new { date = Today, category = "meeting", hours = sent });

        created.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Read(created)).Hours.Should().Be((decimal)expected);

        var listed = (await (await client.GetAsync($"/api/v1/time-entries?from={Today}&to={Today}"))
            .Content.ReadFromJsonAsync<ApiResponse<PagedResultDto<TimeEntryDto>>>(JsonOpts))!.Data!.Items;
        listed.Should().ContainSingle().Which.Hours.Should().Be((decimal)expected, "what is returned now must match what a reload shows");
    }

    [Fact]
    public async Task Two_manual_sessions_in_a_day_add_up_exactly()
    {
        var client = await ClientAsync("two_sessions");
        foreach (var h in new[] { 0.93, 2.49 })      // adds to 3.4200000000000004 in floating point
            (await client.PostAsJsonAsync("/api/v1/time-entries", new { date = Today, category = "meeting", hours = h })).StatusCode.Should().Be(HttpStatusCode.OK);

        var listed = (await (await client.GetAsync($"/api/v1/time-entries?from={Today}&to={Today}"))
            .Content.ReadFromJsonAsync<ApiResponse<PagedResultDto<TimeEntryDto>>>(JsonOpts))!.Data!.Items;

        listed.Sum(e => e.Hours).Should().Be(3.42m);
    }

    [Fact]
    public async Task Editing_an_entry_rounds_the_new_value_the_same_way()
    {
        var client = await ClientAsync("edit");
        var created = await Read(await client.PostAsJsonAsync("/api/v1/time-entries", new { date = Today, category = "admin", hours = 1 }));

        var updated = await client.PutAsJsonAsync($"/api/v1/time-entries/{created.Id}", new { date = Today, category = "admin", hours = 3.4200000000000004 });

        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Read(updated)).Hours.Should().Be(3.42m);
    }

    [Theory]
    [InlineData(0.004)]
    [InlineData(0.0049)]
    public async Task A_sliver_that_would_round_to_zero_is_refused_not_stored(double sent)
    {
        var client = await ClientAsync($"sliver_{sent}");

        var created = await client.PostAsJsonAsync("/api/v1/time-entries", new { date = Today, category = "meeting", hours = sent });

        created.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var listed = (await (await client.GetAsync($"/api/v1/time-entries?from={Today}&to={Today}"))
            .Content.ReadFromJsonAsync<ApiResponse<PagedResultDto<TimeEntryDto>>>(JsonOpts))!.Data!.Items;
        listed.Should().BeEmpty("no zero-hour row may be left behind");
    }
}
