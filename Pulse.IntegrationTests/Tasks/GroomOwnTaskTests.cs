using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Tasks;
using Pulse.Domain.Engineers;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace Pulse.IntegrationTests.Tasks;

[Collection("Integration")]
public class GroomOwnTaskTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public GroomOwnTaskTests(PulseWebApplicationFactory factory) : base(factory) { }

    private async Task<(TaskDto Task, System.Net.Http.HttpClient Client)> SeedSelfCreatedTaskAsync(string email)
    {
        var engineer = await SeedEngineerAsync(email, Roles.Engineer);
        var team = await SeedTeamAsync($"{email}-team");
        await AssignEngineerToTeamAsync(engineer.Id, team.Id);
        var project = await SeedProjectAsync($"{email}-project", team.Id);
        var client = await AuthenticatedClientAsync(email);

        var created = await client.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Self-filed task",
            projectId = project.Id,
            assigneeId = engineer.Id,
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)).ToString("yyyy-MM-dd"),
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = (await created.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;
        return (body, client);
    }

    [Fact]
    public async Task Engineer_created_task_starts_ungroomed_and_in_backlog()
    {
        var (task, _) = await SeedSelfCreatedTaskAsync("groom_seed@pulse.io");

        task.Points.Should().Be(0);
        task.Status.Should().Be("backlog");
    }

    [Fact]
    public async Task Creator_can_groom_their_own_task_and_it_activates()
    {
        var (task, client) = await SeedSelfCreatedTaskAsync("groom_creator@pulse.io");

        var response = await client.PatchAsJsonAsync($"/api/v1/tasks/{task.Id}/groom", new { points = 3, priority = 2 });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;
        body.Points.Should().Be(3);
        body.Priority.Should().Be(2);
        body.Status.Should().Be("active");
    }

    [Fact]
    public async Task Someone_else_cannot_groom_a_task_they_did_not_create()
    {
        var (task, _) = await SeedSelfCreatedTaskAsync("groom_owner2@pulse.io");
        await SeedEngineerAsync("groom_stranger@pulse.io", Roles.Engineer);
        var strangerClient = await AuthenticatedClientAsync("groom_stranger@pulse.io");

        var response = await strangerClient.PatchAsJsonAsync($"/api/v1/tasks/{task.Id}/groom", new { points = 3 });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Cannot_groom_a_task_that_is_already_groomed()
    {
        var (task, client) = await SeedSelfCreatedTaskAsync("groom_twice@pulse.io");
        (await client.PatchAsJsonAsync($"/api/v1/tasks/{task.Id}/groom", new { points = 3 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await client.PatchAsJsonAsync($"/api/v1/tasks/{task.Id}/groom", new { points = 5 });

        second.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Zero_points_is_rejected()
    {
        var (task, client) = await SeedSelfCreatedTaskAsync("groom_zero@pulse.io");

        var response = await client.PatchAsJsonAsync($"/api/v1/tasks/{task.Id}/groom", new { points = 0 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_engineer_still_cannot_call_the_general_patch_endpoint_directly()
    {
        var (task, client) = await SeedSelfCreatedTaskAsync("groom_no_general_patch@pulse.io");

        var response = await client.PatchAsJsonAsync($"/api/v1/tasks/{task.Id}", new { points = 3 });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
