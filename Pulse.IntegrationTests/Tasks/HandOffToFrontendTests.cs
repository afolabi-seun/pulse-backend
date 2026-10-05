using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Tasks;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Tasks;

[Collection("Integration")]
public class HandOffToFrontendTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public HandOffToFrontendTests(PulseWebApplicationFactory factory) : base(factory) { }

    private async Task<Engineer> SeedFrontendEngineerAsync(string email)
    {
        var engineer = await SeedEngineerAsync(email, Roles.Engineer);
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var tracked = await db.Engineers.FindAsync(engineer.Id);
        tracked!.SetDiscipline(Discipline.Frontend);
        await db.SaveChangesAsync();
        return engineer;
    }

    private async Task<Engineer> SeedBackendEngineerAsync(string email)
    {
        var engineer = await SeedEngineerAsync(email, Roles.Engineer);
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        var tracked = await db.Engineers.FindAsync(engineer.Id);
        tracked!.SetDiscipline(Discipline.Backend);
        await db.SaveChangesAsync();
        return engineer;
    }

    [Fact]
    public async Task HandOffToFrontend_reassigns_the_task_without_changing_its_status()
    {
        var backendDev = await SeedEngineerAsync("handoff_backend@pulse.io", Roles.Engineer);
        var frontendDev = await SeedFrontendEngineerAsync("handoff_frontend@pulse.io");
        var project = await SeedProjectAsync("Handoff project");
        await SeedProjectMemberAsync(project.Id, backendDev.Id);
        var backendClient = await AuthenticatedClientAsync("handoff_backend@pulse.io");

        var created = await backendClient.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Two-stage feature",
            points = 5,
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)).ToString("yyyy-MM-dd"),
            projectId = project.Id,
            assigneeId = backendDev.Id,
            requiresFrontendHandoff = true,
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var task = (await created.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;
        task.RequiresFrontendHandoff.Should().BeTrue();
        task.CurrentStage.Should().Be("backend");

        var handOffResp = await backendClient.PostAsJsonAsync(
            $"/api/v1/tasks/{task.Id}/hand-off-to-frontend", new { frontendAssigneeId = frontendDev.Id });

        handOffResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var handedOff = (await handOffResp.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;
        handedOff.AssigneeId.Should().Be(frontendDev.Id);
        handedOff.CurrentStage.Should().Be("frontend");
        // Status is untouched by the handoff — the task never leaves the board column it was
        // already in just because the assignee changed.
        handedOff.Status.Should().Be(task.Status);

        // The frontend engineer's own MarkDone path works completely unmodified afterward.
        var frontendClient = await AuthenticatedClientAsync("handoff_frontend@pulse.io");
        var markDone = await frontendClient.PostAsync($"/api/v1/tasks/{task.Id}/mark-done", null);
        markDone.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task HandOffToFrontend_rejects_a_target_whose_discipline_is_not_Frontend()
    {
        var backendDev = await SeedEngineerAsync("handoff_reject_backend@pulse.io", Roles.Engineer);
        var otherBackendDev = await SeedEngineerAsync("handoff_reject_other@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("Handoff reject project");
        await SeedProjectMemberAsync(project.Id, backendDev.Id);
        var client = await AuthenticatedClientAsync("handoff_reject_backend@pulse.io");

        var created = await client.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Two-stage feature",
            points = 5,
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)).ToString("yyyy-MM-dd"),
            projectId = project.Id,
            assigneeId = backendDev.Id,
            requiresFrontendHandoff = true,
        });
        var task = (await created.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;

        var handOffResp = await client.PostAsJsonAsync(
            $"/api/v1/tasks/{task.Id}/hand-off-to-frontend", new { frontendAssigneeId = otherBackendDev.Id });

        handOffResp.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task HandOffToFrontend_rejects_a_task_that_is_not_flagged()
    {
        var backendDev = await SeedEngineerAsync("handoff_unflagged_backend@pulse.io", Roles.Engineer);
        var frontendDev = await SeedFrontendEngineerAsync("handoff_unflagged_frontend@pulse.io");
        var project = await SeedProjectAsync("Handoff unflagged project");
        await SeedProjectMemberAsync(project.Id, backendDev.Id);
        var client = await AuthenticatedClientAsync("handoff_unflagged_backend@pulse.io");

        var created = await client.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Single-stage feature",
            points = 5,
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)).ToString("yyyy-MM-dd"),
            projectId = project.Id,
            assigneeId = backendDev.Id,
        });
        var task = (await created.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;
        task.RequiresFrontendHandoff.Should().BeFalse();

        var handOffResp = await client.PostAsJsonAsync(
            $"/api/v1/tasks/{task.Id}/hand-off-to-frontend", new { frontendAssigneeId = frontendDev.Id });

        handOffResp.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task HandOffToBackend_reverses_a_prior_HandOffToFrontend()
    {
        var backendDev = await SeedEngineerAsync("handoff_reverse_backend@pulse.io", Roles.Engineer);
        var frontendDev = await SeedFrontendEngineerAsync("handoff_reverse_frontend@pulse.io");
        var anotherBackendDev = await SeedBackendEngineerAsync("handoff_reverse_backend2@pulse.io");
        var project = await SeedProjectAsync("Handoff reverse project");
        await SeedProjectMemberAsync(project.Id, backendDev.Id);
        var backendClient = await AuthenticatedClientAsync("handoff_reverse_backend@pulse.io");

        var created = await backendClient.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Two-stage feature",
            points = 5,
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)).ToString("yyyy-MM-dd"),
            projectId = project.Id,
            assigneeId = backendDev.Id,
            requiresFrontendHandoff = true,
        });
        var task = (await created.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;

        (await backendClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/hand-off-to-frontend",
            new { frontendAssigneeId = frontendDev.Id })).StatusCode.Should().Be(HttpStatusCode.OK);

        var frontendClient = await AuthenticatedClientAsync("handoff_reverse_frontend@pulse.io");
        var handBackResp = await frontendClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/hand-off-to-backend",
            new { backendAssigneeId = anotherBackendDev.Id });

        handBackResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var handedBack = (await handBackResp.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;
        handedBack.AssigneeId.Should().Be(anotherBackendDev.Id);
        handedBack.CurrentStage.Should().Be("backend");
        handedBack.Status.Should().Be(task.Status);
    }
}
