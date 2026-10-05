using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Comments;
using Pulse.Application.Common;
using Pulse.Application.Notifications;
using Pulse.Domain.Engineers;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace Pulse.IntegrationTests.Comments;

[Collection("Integration")]
public class CommentsTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public CommentsTests(PulseWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task AddComment_returns_200_and_the_created_comment()
    {
        var author = await SeedEngineerAsync("comment_author@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("Comment project");
        var task = await SeedTaskAsync("Task with comments", project.Id, assigneeId: author.Id);
        await SeedProjectMemberAsync(project.Id, author.Id);
        var client = await AuthenticatedClientAsync("comment_author@pulse.io");

        var response = await client.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/comments", new { body = "Looks good to me" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<CommentDto>>(JsonOpts);
        body!.Data!.Body.Should().Be("Looks good to me");
        body.Data.AuthorName.Should().Be("Test User");
    }

    [Fact]
    public async Task AddComment_notifies_a_project_member_mentioned_by_full_name()
    {
        var author = await SeedEngineerAsync("mention_author@pulse.io", Roles.Engineer);
        var mentioned = await SeedEngineerAsync("mention_target@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("Mention project");
        var task = await SeedTaskAsync("Task to mention on", project.Id, assigneeId: author.Id);
        await SeedProjectMemberAsync(project.Id, author.Id);
        await SeedProjectMemberAsync(project.Id, mentioned.Id);
        var client = await AuthenticatedClientAsync("mention_author@pulse.io");

        var response = await client.PostAsJsonAsync(
            $"/api/v1/tasks/{task.Id}/comments",
            new { body = $"Hey @{mentioned.Name}, can you take a look?" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var mentionedClient = await AuthenticatedClientAsync("mention_target@pulse.io");
        var notifResponse = await mentionedClient.GetAsync("/api/v1/notifications");
        var notifications = (await notifResponse.Content.ReadFromJsonAsync<ApiResponse<PagedResult<NotificationDto>>>(JsonOpts))!.Data!;

        notifications.Items.Should().Contain(n => n.Kind == "mentioned");
    }

    [Fact]
    public async Task AddComment_does_not_notify_a_name_that_is_not_a_project_member()
    {
        var author = await SeedEngineerAsync("mention_author2@pulse.io", Roles.Engineer);
        var outsider = await SeedEngineerAsync("mention_outsider@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("Mention outsider project");
        var task = await SeedTaskAsync("Task to mention on 2", project.Id, assigneeId: author.Id);
        await SeedProjectMemberAsync(project.Id, author.Id);
        // outsider is intentionally NOT added as a project member
        var client = await AuthenticatedClientAsync("mention_author2@pulse.io");

        var response = await client.PostAsJsonAsync(
            $"/api/v1/tasks/{task.Id}/comments",
            new { body = $"cc @{outsider.Name}" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var outsiderClient = await AuthenticatedClientAsync("mention_outsider@pulse.io");
        var notifResponse = await outsiderClient.GetAsync("/api/v1/notifications");
        var notifications = (await notifResponse.Content.ReadFromJsonAsync<ApiResponse<PagedResult<NotificationDto>>>(JsonOpts))!.Data!;

        notifications.Items.Should().NotContain(n => n.Kind == "mentioned");
    }
}
