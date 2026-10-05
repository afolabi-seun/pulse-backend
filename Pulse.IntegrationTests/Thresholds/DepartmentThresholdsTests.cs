using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Thresholds;
using Pulse.Domain.Engineers;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace Pulse.IntegrationTests.Thresholds;

[Collection("Integration")]
public class DepartmentThresholdsTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public DepartmentThresholdsTests(PulseWebApplicationFactory factory) : base(factory) { }

    // ── access control ────────────────────────────────────────────────────────

    [Fact]
    public async Task Unauthenticated_cannot_get_department_thresholds()
    {
        var response = await Client.GetAsync("/api/v1/thresholds/departments");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Engineer_cannot_get_department_thresholds()
    {
        await SeedEngineerAsync("dept_thr_eng@pulse.io", Roles.Engineer);
        var client = await AuthenticatedClientAsync("dept_thr_eng@pulse.io");

        var response = await client.GetAsync("/api/v1/thresholds/departments");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Head_can_view_but_not_edit_department_thresholds()
    {
        await SeedEngineerAsync("dept_thr_rnd@pulse.io", Roles.HeadOfRnD);
        var client = await AuthenticatedClientAsync("dept_thr_rnd@pulse.io");

        var getResp = await client.GetAsync("/api/v1/thresholds/departments");
        getResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var putResp = await client.PutAsJsonAsync("/api/v1/thresholds/departments/Engineering", new { maxConcurrentTasks = 5 });
        putResp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── upsert / get / delete round trip ─────────────────────────────────────

    [Fact]
    public async Task Head_of_pmo_can_create_view_and_delete_a_department_override()
    {
        await SeedEngineerAsync("dept_thr_pmo@pulse.io", Roles.HeadOfPmo);
        var client = await AuthenticatedClientAsync("dept_thr_pmo@pulse.io");

        var putResp = await client.PutAsJsonAsync("/api/v1/thresholds/departments/Core%20Banking",
            new { loadVsBaselineRatio = 2.0, maxConcurrentTasks = 5 });

        putResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var putBody = await putResp.Content.ReadFromJsonAsync<ApiResponse<DepartmentThresholdDto>>(JsonOpts);
        putBody!.Data!.Department.Should().Be("Core Banking");
        putBody.Data.LoadVsBaselineRatio.Should().Be(2.0);
        putBody.Data.MaxConcurrentTasks.Should().Be(5);
        putBody.Data.StaleCycleMultiplier.Should().BeNull("not set — inherits the global default");

        var getResp = await client.GetAsync("/api/v1/thresholds/departments");
        var getBody = await getResp.Content.ReadFromJsonAsync<ApiResponse<IReadOnlyList<DepartmentThresholdDto>>>(JsonOpts);
        getBody!.Data.Should().ContainSingle(d => d.Department == "Core Banking" && d.LoadVsBaselineRatio == 2.0);

        var deleteResp = await client.DeleteAsync("/api/v1/thresholds/departments/Core%20Banking");
        deleteResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var afterDeleteResp = await client.GetAsync("/api/v1/thresholds/departments");
        var afterDeleteBody = await afterDeleteResp.Content.ReadFromJsonAsync<ApiResponse<IReadOnlyList<DepartmentThresholdDto>>>(JsonOpts);
        afterDeleteBody!.Data.Should().NotContain(d => d.Department == "Core Banking");
    }

    [Fact]
    public async Task Upserting_the_same_department_twice_replaces_the_override()
    {
        await SeedEngineerAsync("dept_thr_replace@pulse.io", Roles.HeadOfPmo);
        var client = await AuthenticatedClientAsync("dept_thr_replace@pulse.io");

        await client.PutAsJsonAsync("/api/v1/thresholds/departments/Design", new { loadVsBaselineRatio = 2.0, maxConcurrentTasks = 5 });

        // Second call omits maxConcurrentTasks — it should revert to "inherits global", not keep 5.
        var secondResp = await client.PutAsJsonAsync("/api/v1/thresholds/departments/Design", new { loadVsBaselineRatio = 1.8 });

        var body = await secondResp.Content.ReadFromJsonAsync<ApiResponse<DepartmentThresholdDto>>(JsonOpts);
        body!.Data!.LoadVsBaselineRatio.Should().Be(1.8);
        body.Data.MaxConcurrentTasks.Should().BeNull("upsert replaces the whole override, it doesn't patch individual fields");
    }

    [Fact]
    public async Task Upsert_returns_400_when_ratio_is_zero()
    {
        await SeedEngineerAsync("dept_thr_val@pulse.io", Roles.HeadOfPmo);
        var client = await AuthenticatedClientAsync("dept_thr_val@pulse.io");

        var response = await client.PutAsJsonAsync("/api/v1/thresholds/departments/Product", new { loadVsBaselineRatio = 0.0 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Deleting_an_override_that_does_not_exist_still_returns_204()
    {
        await SeedEngineerAsync("dept_thr_del_noop@pulse.io", Roles.HeadOfPmo);
        var client = await AuthenticatedClientAsync("dept_thr_del_noop@pulse.io");

        var response = await client.DeleteAsync("/api/v1/thresholds/departments/NeverExisted");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
