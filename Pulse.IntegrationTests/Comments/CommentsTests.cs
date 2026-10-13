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

    // ── Executives ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_executive_can_comment_on_a_task_in_a_project_they_are_not_a_member_of_and_the_assignee_is_told()
    {
        var assignee = await SeedEngineerAsync("exec_cmt_assignee@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("exec_cmt_exec@pulse.io", Roles.Executive);
        var project = await SeedProjectAsync("Executive comments");
        var task = await SeedTaskAsync("Treasury go-live", project.Id, assigneeId: assignee.Id);
        await SeedProjectMemberAsync(project.Id, assignee.Id);
        var exec = await AuthenticatedClientAsync("exec_cmt_exec@pulse.io");

        var response = await exec.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/comments", new { body = "Please make this the priority" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<ApiResponse<CommentDto>>(JsonOpts))!.Data!.Body.Should().Be("Please make this the priority");

        // Everyone on the task sees it.
        var assigneeClient = await AuthenticatedClientAsync("exec_cmt_assignee@pulse.io");
        var thread = (await (await assigneeClient.GetAsync($"/api/v1/tasks/{task.Id}/comments"))
            .Content.ReadFromJsonAsync<ApiResponse<List<CommentDto>>>(JsonOpts))!.Data!;
        thread.Should().Contain(c => c.Body == "Please make this the priority");

        // And the assignee is told, without having been @mentioned.
        var notifications = (await (await assigneeClient.GetAsync("/api/v1/notifications"))
            .Content.ReadFromJsonAsync<ApiResponse<PagedResult<NotificationDto>>>(JsonOpts))!.Data!;
        notifications.Items.Should().Contain(n => n.Kind == "executive_commented");
    }

    [Fact]
    public async Task An_executive_can_edit_their_own_comment_but_cannot_edit_a_task()
    {
        var assignee = await SeedEngineerAsync("exec_cmt_assignee2@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("exec_cmt_exec2@pulse.io", Roles.Executive);
        var project = await SeedProjectAsync("Executive edits");
        var task = await SeedTaskAsync("Treasury phase two", project.Id, assigneeId: assignee.Id);
        var exec = await AuthenticatedClientAsync("exec_cmt_exec2@pulse.io");
        var added = (await (await exec.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/comments", new { body = "first thought" }))
            .Content.ReadFromJsonAsync<ApiResponse<CommentDto>>(JsonOpts))!.Data!;

        var edit = await exec.PatchAsJsonAsync($"/api/v1/tasks/{task.Id}/comments/{added.Id}", new { body = "better thought" });
        var taskEdit = await exec.PatchAsJsonAsync($"/api/v1/tasks/{task.Id}", new { title = "Renamed by an executive" });

        edit.StatusCode.Should().Be(HttpStatusCode.OK);
        taskEdit.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_executive_cannot_comment_on_a_personal_task()
    {
        var owner = await SeedEngineerAsync("exec_cmt_personal_owner@pulse.io", Roles.HR);
        await SeedEngineerAsync("exec_cmt_exec3@pulse.io", Roles.Executive);
        var hr = await AuthenticatedClientAsync("exec_cmt_personal_owner@pulse.io");
        var due = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)).ToString("yyyy-MM-dd");
        var created = (await (await hr.PostAsJsonAsync("/api/v1/tasks", new { title = "Private to-do", personal = true, dueDate = due }))
            .Content.ReadFromJsonAsync<ApiResponse<Pulse.Application.Tasks.TaskDto>>(JsonOpts))!.Data!;
        var exec = await AuthenticatedClientAsync("exec_cmt_exec3@pulse.io");

        var response = await exec.PostAsJsonAsync($"/api/v1/tasks/{created.Id}/comments", new { body = "snooping" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        owner.Id.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData(Roles.HR)]
    [InlineData(Roles.Accountant)]
    public async Task Hr_and_accountant_still_cannot_comment_on_other_peoples_tasks(string role)
    {
        var assignee = await SeedEngineerAsync($"exec_cmt_{role}_assignee@pulse.io", Roles.Engineer);
        await SeedEngineerAsync($"exec_cmt_{role}@pulse.io", role);
        var project = await SeedProjectAsync($"Read only {role}");
        var task = await SeedTaskAsync("Someone else's work", project.Id, assigneeId: assignee.Id);
        var client = await AuthenticatedClientAsync($"exec_cmt_{role}@pulse.io");

        var response = await client.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/comments", new { body = "hello" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
