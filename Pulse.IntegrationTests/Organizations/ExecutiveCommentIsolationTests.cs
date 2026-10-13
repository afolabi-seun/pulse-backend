using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Comments;
using Pulse.Application.Common;
using Pulse.Application.Notifications;
using Pulse.Domain.Engineers;
using Pulse.Domain.Organizations;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Organizations;

/// <summary>
/// An Executive may comment on any task — in their own organization. The rule is an exception in the comment handler, so what keeps it from reaching
/// another organization's tasks is the organization filter on the task lookup; these tests pin that.
/// </summary>
[Collection("Integration")]
public class ExecutiveCommentIsolationTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public ExecutiveCommentIsolationTests(PulseWebApplicationFactory factory) : base(factory) { }

    private async Task MoveToOrganizationAsync(object entity, Guid organizationId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        db.Attach(entity);
        db.Entry(entity).Property("OrganizationId").CurrentValue = organizationId;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task An_executive_comments_in_their_own_organization_but_not_in_another()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];

        Guid orgBId;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var orgB = Organization.Create("Exec comment org B", $"exec-b-{suffix}");
            db.Organizations.Add(orgB);
            await db.SaveChangesAsync();
            orgBId = orgB.Id;
        }

        // Org A: an assignee and a task. Org B: an executive.
        var assignee = await SeedEngineerAsync($"execiso_assignee_{suffix}@pulse.io", Roles.Engineer);
        await SeedEngineerAsync($"execiso_execA_{suffix}@pulse.io", Roles.Executive);
        var execB = await SeedEngineerAsync($"execiso_execB_{suffix}@pulse.io", Roles.Executive);
        await MoveToOrganizationAsync(execB, orgBId);
        var project = await SeedProjectAsync($"Exec isolation {suffix}");
        var task = await SeedTaskAsync($"Org A task {suffix}", project.Id, assigneeId: assignee.Id);

        var execAClient = await AuthenticatedClientAsync($"execiso_execA_{suffix}@pulse.io");
        var execBClient = await AuthenticatedClientAsync($"execiso_execB_{suffix}@pulse.io");

        // Control: the executive in the task's own organization can comment, and the assignee is told.
        (await execAClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/comments", new { body = "From org A's executive" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // The executive in another organization cannot, and learns nothing about the task.
        var crossOrg = await execBClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/comments", new { body = "From org B's executive" });
        crossOrg.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var assigneeClient = await AuthenticatedClientAsync($"execiso_assignee_{suffix}@pulse.io");
        var thread = (await (await assigneeClient.GetAsync($"/api/v1/tasks/{task.Id}/comments"))
            .Content.ReadFromJsonAsync<ApiResponse<List<CommentDto>>>(JsonOpts))!.Data!;
        thread.Should().Contain(c => c.Body == "From org A's executive");
        thread.Should().NotContain(c => c.Body == "From org B's executive");

        var notifications = (await (await assigneeClient.GetAsync("/api/v1/notifications"))
            .Content.ReadFromJsonAsync<ApiResponse<PagedResult<NotificationDto>>>(JsonOpts))!.Data!;
        notifications.Items.Should().ContainSingle(n => n.Kind == "executive_commented");
    }
}
