using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Projects.Queries;
using Pulse.Application.Tasks;
using Pulse.Domain.Engineers;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Tasks;

[Collection("Integration")]
public class SubtasksTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public SubtasksTests(PulseWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task Assignee_can_add_a_subtask()
    {
        var engineer = await SeedEngineerAsync("subtask_assignee@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("Subtask project");
        var task = await SeedTaskAsync("Parent task", project.Id, assigneeId: engineer.Id);
        var client = await AuthenticatedClientAsync("subtask_assignee@pulse.io");

        var response = await client.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/subtasks", new { title = "Write tests" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<SubtaskDto>>(JsonOpts);
        body!.Data!.Title.Should().Be("Write tests");
        body.Data.IsDone.Should().BeFalse();
        body.Data.TaskId.Should().Be(task.Id);
    }

    [Fact]
    public async Task TeamLead_can_add_a_subtask_to_a_task_they_are_not_assigned_to()
    {
        var engineer = await SeedEngineerAsync("subtask_owner@pulse.io", Roles.Engineer);
        var lead = await SeedEngineerAsync("subtask_lead@pulse.io", Roles.TeamLead);
        var project = await SeedProjectAsync("Subtask lead project");
        var task = await SeedTaskAsync("Parent task", project.Id, assigneeId: engineer.Id);
        var client = await AuthenticatedClientAsync("subtask_lead@pulse.io");

        var response = await client.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/subtasks", new { title = "Review PR" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Unrelated_engineer_cannot_add_a_subtask()
    {
        var owner = await SeedEngineerAsync("subtask_target_owner@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("subtask_outsider@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("Subtask outsider project");
        var task = await SeedTaskAsync("Parent task", project.Id, assigneeId: owner.Id);
        var client = await AuthenticatedClientAsync("subtask_outsider@pulse.io");

        var response = await client.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/subtasks", new { title = "Sneaky item" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Toggling_a_subtask_done_records_who_and_when()
    {
        var engineer = await SeedEngineerAsync("subtask_toggle@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("Subtask toggle project");
        var task = await SeedTaskAsync("Parent task", project.Id, assigneeId: engineer.Id);
        var client = await AuthenticatedClientAsync("subtask_toggle@pulse.io");

        var created = await client.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/subtasks", new { title = "Do the thing" });
        var subtask = (await created.Content.ReadFromJsonAsync<ApiResponse<SubtaskDto>>(JsonOpts))!.Data!;

        var response = await client.PatchAsJsonAsync($"/api/v1/tasks/{task.Id}/subtasks/{subtask.Id}", new { isDone = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<ApiResponse<SubtaskDto>>(JsonOpts))!.Data!;
        body.IsDone.Should().BeTrue();
        body.CompletedBy.Should().Be(engineer.Id);
        body.CompletedAt.Should().NotBeNull();

        var unchecked_ = await client.PatchAsJsonAsync($"/api/v1/tasks/{task.Id}/subtasks/{subtask.Id}", new { isDone = false });
        var uncheckedBody = (await unchecked_.Content.ReadFromJsonAsync<ApiResponse<SubtaskDto>>(JsonOpts))!.Data!;
        uncheckedBody.IsDone.Should().BeFalse();
        uncheckedBody.CompletedBy.Should().BeNull();
        uncheckedBody.CompletedAt.Should().BeNull();
    }

    [Fact]
    public async Task Completing_a_subtask_surfaces_in_the_project_activity_feed_but_unchecking_it_does_not()
    {
        var engineer = await SeedEngineerAsync("subtask_activity@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("Subtask activity project");
        var task = await SeedTaskAsync("Parent task", project.Id, assigneeId: engineer.Id);
        var client = await AuthenticatedClientAsync("subtask_activity@pulse.io");

        var created = await client.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/subtasks", new { title = "Write the migration" });
        var subtask = (await created.Content.ReadFromJsonAsync<ApiResponse<SubtaskDto>>(JsonOpts))!.Data!;

        await client.PatchAsJsonAsync($"/api/v1/tasks/{task.Id}/subtasks/{subtask.Id}", new { isDone = true });

        var activityResp = await client.GetAsync($"/api/v1/projects/{project.Id}/activity");
        var activity = (await activityResp.Content.ReadFromJsonAsync<ApiResponse<PagedResult<ProjectActivityDto>>>(JsonOpts))!.Data!;
        activity.Items.Should().ContainSingle(i => i.TaskId == task.Id && i.Summary == "completed subtask 'Write the migration'");

        await client.PatchAsJsonAsync($"/api/v1/tasks/{task.Id}/subtasks/{subtask.Id}", new { isDone = false });

        var activityAfterUncheckResp = await client.GetAsync($"/api/v1/projects/{project.Id}/activity");
        var activityAfterUncheck = (await activityAfterUncheckResp.Content.ReadFromJsonAsync<ApiResponse<PagedResult<ProjectActivityDto>>>(JsonOpts))!.Data!;
        activityAfterUncheck.Items.Should().ContainSingle(i => i.TaskId == task.Id && i.Summary.StartsWith("completed subtask"),
            "unchecking a subtask is deliberately left unaudited, so no second entry should appear");
    }

    [Fact]
    public async Task Unrelated_engineer_cannot_toggle_a_subtask()
    {
        var owner = await SeedEngineerAsync("subtask_toggle_owner@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("subtask_toggle_outsider@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("Subtask toggle outsider project");
        var task = await SeedTaskAsync("Parent task", project.Id, assigneeId: owner.Id);
        var ownerClient = await AuthenticatedClientAsync("subtask_toggle_owner@pulse.io");
        var created = await ownerClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/subtasks", new { title = "Item" });
        var subtask = (await created.Content.ReadFromJsonAsync<ApiResponse<SubtaskDto>>(JsonOpts))!.Data!;

        var outsiderClient = await AuthenticatedClientAsync("subtask_toggle_outsider@pulse.io");
        var response = await outsiderClient.PatchAsJsonAsync($"/api/v1/tasks/{task.Id}/subtasks/{subtask.Id}", new { isDone = true });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Borrower_can_view_and_toggle_a_subtask_loaned_to_them_without_other_task_access()
    {
        // Complements Unrelated_engineer_cannot_toggle_a_subtask above — the one exception is a
        // subtask specifically loaned to that engineer (GetSubtasksQuery/ToggleSubtaskHandler both
        // check subtask.AssigneeId, not just the parent task's).
        var lead = await SeedEngineerAsync("subtask_loan_lead@pulse.io", Roles.TeamLead);
        var owner = await SeedEngineerAsync("subtask_loan_owner@pulse.io", Roles.Engineer);
        var borrower = await SeedEngineerAsync("subtask_loan_borrower@pulse.io", Roles.Engineer);
        var teamA = await SeedTeamAsync("Loan Subtask Team A", lead.Id);
        var teamB = await SeedTeamAsync("Loan Subtask Team B");

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Pulse.Infrastructure.Persistence.PulseDbContext>();
            var ownerEng = await db.Engineers.FindAsync(owner.Id);
            ownerEng!.AssignToTeam(teamA.Id);
            var borrowerEng = await db.Engineers.FindAsync(borrower.Id);
            borrowerEng!.AssignToTeam(teamB.Id);
            await db.SaveChangesAsync();
        }

        var project = await SeedProjectAsync("Loan subtask access project");
        var task = await SeedTaskAsync("Parent task", project.Id, assigneeId: owner.Id);
        var leadClient = await AuthenticatedClientAsync("subtask_loan_lead@pulse.io");
        var ownerClient = await AuthenticatedClientAsync("subtask_loan_owner@pulse.io");

        var created = await ownerClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/subtasks", new { title = "Cross-team item" });
        var subtask = (await created.Content.ReadFromJsonAsync<ApiResponse<SubtaskDto>>(JsonOpts))!.Data!;

        var loanResp = await leadClient.PostAsJsonAsync(
            $"/api/v1/tasks/{task.Id}/subtasks/{subtask.Id}/loan",
            new { targetEngineerId = borrower.Id, reason = "Cross-team help" });
        loanResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var borrowerClient = await AuthenticatedClientAsync("subtask_loan_borrower@pulse.io");

        var getResp = await borrowerClient.GetAsync($"/api/v1/tasks/{task.Id}/subtasks");
        getResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var toggleResp = await borrowerClient.PatchAsJsonAsync($"/api/v1/tasks/{task.Id}/subtasks/{subtask.Id}", new { isDone = true });
        toggleResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var toggled = (await toggleResp.Content.ReadFromJsonAsync<ApiResponse<SubtaskDto>>(JsonOpts))!.Data!;
        toggled.IsDone.Should().BeTrue();
        toggled.AssigneeId.Should().Be(borrower.Id);

        // The loan grants real project membership (LoanSubtaskHandler's AddMemberAsync side
        // effect), so the borrower can also open the parent task page itself, not just the
        // subtask endpoints — no separate access carve-out is needed for this.
        var taskGetResp = await borrowerClient.GetAsync($"/api/v1/tasks/{task.Id}");
        taskGetResp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Loaned_subtask_can_be_recalled_back_to_unassigned()
    {
        var lead = await SeedEngineerAsync("subtask_recall_lead@pulse.io", Roles.TeamLead);
        var owner = await SeedEngineerAsync("subtask_recall_owner@pulse.io", Roles.Engineer);
        var borrower = await SeedEngineerAsync("subtask_recall_borrower@pulse.io", Roles.Engineer);
        var teamA = await SeedTeamAsync("Recall Subtask Team A", lead.Id);
        var teamB = await SeedTeamAsync("Recall Subtask Team B");

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Pulse.Infrastructure.Persistence.PulseDbContext>();
            var ownerEng = await db.Engineers.FindAsync(owner.Id);
            ownerEng!.AssignToTeam(teamA.Id);
            var borrowerEng = await db.Engineers.FindAsync(borrower.Id);
            borrowerEng!.AssignToTeam(teamB.Id);
            await db.SaveChangesAsync();
        }

        var project = await SeedProjectAsync("Recall subtask project");
        var task = await SeedTaskAsync("Parent task", project.Id, assigneeId: owner.Id);
        var leadClient = await AuthenticatedClientAsync("subtask_recall_lead@pulse.io");
        var ownerClient = await AuthenticatedClientAsync("subtask_recall_owner@pulse.io");

        var created = await ownerClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/subtasks", new { title = "Loaned then recalled" });
        var subtask = (await created.Content.ReadFromJsonAsync<ApiResponse<SubtaskDto>>(JsonOpts))!.Data!;

        var loanResp = await leadClient.PostAsJsonAsync(
            $"/api/v1/tasks/{task.Id}/subtasks/{subtask.Id}/loan",
            new { targetEngineerId = borrower.Id });
        loanResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var recallResp = await leadClient.PostAsync($"/api/v1/tasks/{task.Id}/subtasks/{subtask.Id}/recall", null);

        recallResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var recalled = (await recallResp.Content.ReadFromJsonAsync<ApiResponse<SubtaskDto>>(JsonOpts))!.Data!;
        recalled.AssigneeId.Should().BeNull();
        recalled.AssigneeName.Should().BeNull();

        // The recalled borrower no longer has a claim to this subtask via subtask.AssigneeId —
        // toggling it again should now be forbidden for them (mirrors Unrelated_engineer_cannot_toggle_a_subtask).
        var borrowerClient = await AuthenticatedClientAsync("subtask_recall_borrower@pulse.io");
        var toggleAfterRecall = await borrowerClient.PatchAsJsonAsync($"/api/v1/tasks/{task.Id}/subtasks/{subtask.Id}", new { isDone = true });
        toggleAfterRecall.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task RecallSubtask_lets_the_current_borrower_recall_it_themselves_with_no_lead_or_head_role()
    {
        // Same HTTP-boundary regression coverage as RecallTask's — the endpoint used to carry its
        // own role-only [RequiresCapability] gate that would 403 a plain Engineer before the
        // handler's self-recall check ever ran.
        var lead = await SeedEngineerAsync("subtask_recall_self_lead@pulse.io", Roles.TeamLead);
        var owner = await SeedEngineerAsync("subtask_recall_self_owner@pulse.io", Roles.Engineer);
        var borrower = await SeedEngineerAsync("subtask_recall_self_borrower@pulse.io", Roles.Engineer);
        var teamA = await SeedTeamAsync("Recall Self Subtask Team A", lead.Id);
        var teamB = await SeedTeamAsync("Recall Self Subtask Team B");

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Pulse.Infrastructure.Persistence.PulseDbContext>();
            var ownerEng = await db.Engineers.FindAsync(owner.Id);
            ownerEng!.AssignToTeam(teamA.Id);
            var borrowerEng = await db.Engineers.FindAsync(borrower.Id);
            borrowerEng!.AssignToTeam(teamB.Id);
            await db.SaveChangesAsync();
        }

        var project = await SeedProjectAsync("Recall self subtask project");
        var task = await SeedTaskAsync("Parent task", project.Id, assigneeId: owner.Id);
        var leadClient = await AuthenticatedClientAsync("subtask_recall_self_lead@pulse.io");
        var ownerClient = await AuthenticatedClientAsync("subtask_recall_self_owner@pulse.io");

        var created = await ownerClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/subtasks", new { title = "Loaned then self-recalled" });
        var subtask = (await created.Content.ReadFromJsonAsync<ApiResponse<SubtaskDto>>(JsonOpts))!.Data!;

        var loanResp = await leadClient.PostAsJsonAsync(
            $"/api/v1/tasks/{task.Id}/subtasks/{subtask.Id}/loan",
            new { targetEngineerId = borrower.Id });
        loanResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var borrowerClient = await AuthenticatedClientAsync("subtask_recall_self_borrower@pulse.io");
        var recallResp = await borrowerClient.PostAsync($"/api/v1/tasks/{task.Id}/subtasks/{subtask.Id}/recall", null);

        recallResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var recalled = (await recallResp.Content.ReadFromJsonAsync<ApiResponse<SubtaskDto>>(JsonOpts))!.Data!;
        recalled.AssigneeId.Should().BeNull();
    }

    [Fact]
    public async Task RecallSubtask_returns_422_when_not_currently_loaned()
    {
        var lead = await SeedEngineerAsync("subtask_recall_noop_lead@pulse.io", Roles.HeadOfRnD);
        var owner = await SeedEngineerAsync("subtask_recall_noop_owner@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("Recall noop project");
        var task = await SeedTaskAsync("Parent task", project.Id, assigneeId: owner.Id);
        var ownerClient = await AuthenticatedClientAsync("subtask_recall_noop_owner@pulse.io");
        var leadClient = await AuthenticatedClientAsync("subtask_recall_noop_lead@pulse.io");

        var created = await ownerClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/subtasks", new { title = "Never loaned" });
        var subtask = (await created.Content.ReadFromJsonAsync<ApiResponse<SubtaskDto>>(JsonOpts))!.Data!;

        var recallResp = await leadClient.PostAsync($"/api/v1/tasks/{task.Id}/subtasks/{subtask.Id}/recall", null);

        recallResp.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Assignee_can_delete_a_subtask()
    {
        var engineer = await SeedEngineerAsync("subtask_delete@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("Subtask delete project");
        var task = await SeedTaskAsync("Parent task", project.Id, assigneeId: engineer.Id);
        var client = await AuthenticatedClientAsync("subtask_delete@pulse.io");
        var created = await client.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/subtasks", new { title = "Temp item" });
        var subtask = (await created.Content.ReadFromJsonAsync<ApiResponse<SubtaskDto>>(JsonOpts))!.Data!;

        var response = await client.DeleteAsync($"/api/v1/tasks/{task.Id}/subtasks/{subtask.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var list = await client.GetAsync($"/api/v1/tasks/{task.Id}/subtasks");
        var items = (await list.Content.ReadFromJsonAsync<ApiResponse<IReadOnlyList<SubtaskDto>>>(JsonOpts))!.Data!;
        items.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSubtasks_returns_items_in_creation_order()
    {
        var engineer = await SeedEngineerAsync("subtask_order@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("Subtask order project");
        var task = await SeedTaskAsync("Parent task", project.Id, assigneeId: engineer.Id);
        var client = await AuthenticatedClientAsync("subtask_order@pulse.io");
        await client.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/subtasks", new { title = "First" });
        await client.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/subtasks", new { title = "Second" });
        await client.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/subtasks", new { title = "Third" });

        var response = await client.GetAsync($"/api/v1/tasks/{task.Id}/subtasks");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = (await response.Content.ReadFromJsonAsync<ApiResponse<IReadOnlyList<SubtaskDto>>>(JsonOpts))!.Data!;
        items.Select(i => i.Title).Should().Equal("First", "Second", "Third");
    }

    [Fact]
    public async Task Adding_a_subtask_with_blank_title_returns_400()
    {
        var engineer = await SeedEngineerAsync("subtask_blank@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("Subtask blank project");
        var task = await SeedTaskAsync("Parent task", project.Id, assigneeId: engineer.Id);
        var client = await AuthenticatedClientAsync("subtask_blank@pulse.io");

        var response = await client.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/subtasks", new { title = "   " });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Loaning_a_subtask_with_a_missing_target_engineer_returns_400()
    {
        var lead = await SeedEngineerAsync("subtask_loan_validation_lead@pulse.io", Roles.TeamLead);
        var owner = await SeedEngineerAsync("subtask_loan_validation_owner@pulse.io", Roles.Engineer);
        var team = await SeedTeamAsync("Loan Subtask Validation Team", lead.Id);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Pulse.Infrastructure.Persistence.PulseDbContext>();
            var ownerEng = await db.Engineers.FindAsync(owner.Id);
            ownerEng!.AssignToTeam(team.Id);
            await db.SaveChangesAsync();
        }

        var project = await SeedProjectAsync("Loan subtask validation project");
        var task = await SeedTaskAsync("Parent task", project.Id, assigneeId: owner.Id);
        var ownerClient = await AuthenticatedClientAsync("subtask_loan_validation_owner@pulse.io");
        var leadClient = await AuthenticatedClientAsync("subtask_loan_validation_lead@pulse.io");

        var created = await ownerClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/subtasks", new { title = "Needs a target" });
        var subtask = (await created.Content.ReadFromJsonAsync<ApiResponse<SubtaskDto>>(JsonOpts))!.Data!;

        var response = await leadClient.PostAsJsonAsync(
            $"/api/v1/tasks/{task.Id}/subtasks/{subtask.Id}/loan",
            new { targetEngineerId = Guid.Empty });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
