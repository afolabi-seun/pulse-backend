using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Thresholds;
using Pulse.Domain.Engineers;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace Pulse.IntegrationTests.Thresholds;

[Collection("Integration")]
public class ThresholdsTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public ThresholdsTests(PulseWebApplicationFactory factory) : base(factory) { }

    // ── access control ────────────────────────────────────────────────────────

    [Fact]
    public async Task Unauthenticated_cannot_get_thresholds()
    {
        var response = await Client.GetAsync("/api/v1/thresholds");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Engineer_cannot_get_thresholds()
    {
        await SeedEngineerAsync("thr_eng@pulse.io", Roles.Engineer);
        var client = await AuthenticatedClientAsync("thr_eng@pulse.io");

        var response = await client.GetAsync("/api/v1/thresholds");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PM_cannot_get_thresholds()
    {
        await SeedEngineerAsync("thr_pm@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("thr_pm@pulse.io");

        var response = await client.GetAsync("/api/v1/thresholds");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── get ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Head_can_get_thresholds()
    {
        await SeedEngineerAsync("thr_head_get@pulse.io", Roles.HeadOfPmo);
        var client = await AuthenticatedClientAsync("thr_head_get@pulse.io");

        var response = await client.GetAsync("/api/v1/thresholds");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<ThresholdsDto>>(JsonOpts);
        body!.Data.Should().NotBeNull();
        body.Data!.LoadVsBaselineRatio.Should().BeGreaterThan(0);
        body.Data.MaxConcurrentTasks.Should().BeGreaterThan(0);
        body.Data.EscalationT3Days.Should().BeGreaterThan(0);
    }

    // ── update ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Head_can_update_thresholds_and_changes_persist()
    {
        await SeedEngineerAsync("thr_head_upd@pulse.io", Roles.HeadOfPmo);
        var client = await AuthenticatedClientAsync("thr_head_upd@pulse.io");

        var patchResp = await client.PatchAsJsonAsync("/api/v1/thresholds", new
        {
            maxConcurrentTasks = 7
        });

        patchResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var patchBody = await patchResp.Content.ReadFromJsonAsync<ApiResponse<ThresholdsDto>>(JsonOpts);
        patchBody!.Data!.MaxConcurrentTasks.Should().Be(7);

        // Verify the updated value is returned on a subsequent GET
        var getResp = await client.GetAsync("/api/v1/thresholds");
        var getBody = await getResp.Content.ReadFromJsonAsync<ApiResponse<ThresholdsDto>>(JsonOpts);
        getBody!.Data!.MaxConcurrentTasks.Should().Be(7);
    }

    [Fact]
    public async Task Update_thresholds_accepts_partial_patch()
    {
        await SeedEngineerAsync("thr_head_partial@pulse.io", Roles.HeadOfPmo);
        var client = await AuthenticatedClientAsync("thr_head_partial@pulse.io");

        // Fetch the current values before patching
        var beforeBody = (await (await client.GetAsync("/api/v1/thresholds"))
            .Content.ReadFromJsonAsync<ApiResponse<ThresholdsDto>>(JsonOpts))!.Data!;

        // Patch only EscalationT3Days
        await client.PatchAsJsonAsync("/api/v1/thresholds", new { escalationT3Days = 4.0 });

        var afterBody = (await (await client.GetAsync("/api/v1/thresholds"))
            .Content.ReadFromJsonAsync<ApiResponse<ThresholdsDto>>(JsonOpts))!.Data!;

        afterBody.EscalationT3Days.Should().Be(4.0);
        // Other fields unchanged
        afterBody.LoadVsBaselineRatio.Should().Be(beforeBody.LoadVsBaselineRatio);
        afterBody.MaxConcurrentTasks.Should().Be(beforeBody.MaxConcurrentTasks);
    }

    [Fact]
    public async Task Update_thresholds_returns_400_when_ratio_is_zero()
    {
        await SeedEngineerAsync("thr_head_val@pulse.io", Roles.HeadOfPmo);
        var client = await AuthenticatedClientAsync("thr_head_val@pulse.io");

        var response = await client.PatchAsJsonAsync("/api/v1/thresholds", new
        {
            loadVsBaselineRatio = 0.0
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_thresholds_returns_400_when_elapsed_pct_out_of_range()
    {
        await SeedEngineerAsync("thr_head_pct@pulse.io", Roles.HeadOfPmo);
        var client = await AuthenticatedClientAsync("thr_head_pct@pulse.io");

        var response = await client.PatchAsJsonAsync("/api/v1/thresholds", new
        {
            escalationT3ElapsedPct = 1.5  // must be in 0-1
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_thresholds_accepts_partial_patch_for_priority_scale()
    {
        await SeedEngineerAsync("thr_head_prio@pulse.io", Roles.HeadOfPmo);
        var client = await AuthenticatedClientAsync("thr_head_prio@pulse.io");

        var patchResp = await client.PatchAsJsonAsync("/api/v1/thresholds", new
        {
            priorityScale = new[]
            {
                new { value = 1, label = "Critical", criteria = "Drop everything" },
                new { value = 2, label = "High", criteria = "This cycle" },
            }
        });

        patchResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var patchBody = await patchResp.Content.ReadFromJsonAsync<ApiResponse<ThresholdsDto>>(JsonOpts);
        patchBody!.Data!.PriorityScale.Should().ContainSingle(e => e.Value == 1 && e.Label == "Critical" && e.Criteria == "Drop everything");

        var getResp = await client.GetAsync("/api/v1/thresholds");
        var getBody = await getResp.Content.ReadFromJsonAsync<ApiResponse<ThresholdsDto>>(JsonOpts);
        getBody!.Data!.PriorityScale.Should().HaveCount(2, "the whole scale was replaced, not patched entry-by-entry");
    }

    [Fact]
    public async Task Update_thresholds_returns_400_when_priority_scale_value_out_of_range()
    {
        await SeedEngineerAsync("thr_head_prio_range@pulse.io", Roles.HeadOfPmo);
        var client = await AuthenticatedClientAsync("thr_head_prio_range@pulse.io");

        var response = await client.PatchAsJsonAsync("/api/v1/thresholds", new
        {
            priorityScale = new[] { new { value = 9, label = "Nope", criteria = "Out of range" } }
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_thresholds_returns_400_when_priority_scale_values_are_not_distinct()
    {
        await SeedEngineerAsync("thr_head_prio_dup@pulse.io", Roles.HeadOfPmo);
        var client = await AuthenticatedClientAsync("thr_head_prio_dup@pulse.io");

        var response = await client.PatchAsJsonAsync("/api/v1/thresholds", new
        {
            priorityScale = new[]
            {
                new { value = 1, label = "Critical", criteria = "A" },
                new { value = 1, label = "Also critical", criteria = "B" },
            }
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Engineer_cannot_update_thresholds()
    {
        await SeedEngineerAsync("thr_eng_upd@pulse.io", Roles.Engineer);
        var client = await AuthenticatedClientAsync("thr_eng_upd@pulse.io");

        var response = await client.PatchAsJsonAsync("/api/v1/thresholds", new { maxConcurrentTasks = 5 });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ProjectManager_cannot_update_thresholds()
    {
        // Threshold edits are restricted to the Head of PMO only — a project manager
        // (also in the PMO group) may view but not change them.
        await SeedEngineerAsync("thr_pm_upd@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("thr_pm_upd@pulse.io");

        var response = await client.PatchAsJsonAsync("/api/v1/thresholds", new { maxConcurrentTasks = 5 });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
