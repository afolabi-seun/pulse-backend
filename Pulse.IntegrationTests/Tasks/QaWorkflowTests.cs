using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Notifications;
using Pulse.Application.Tasks;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Tasks;

[Collection("Integration")]
public class QaWorkflowTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public QaWorkflowTests(PulseWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task SendToQa_notifies_the_auto_assigned_reviewer()
    {
        var team = await SeedTeamAsync("QA notify team", department: "Engineering");
        var reviewer = await SeedEngineerAsync("qa_notify_reviewer@pulse.io", Roles.Engineer, isQa: true);
        await AssignEngineerToTeamAsync(reviewer.Id, team.Id);

        var owner = await SeedEngineerAsync("qa_notify_owner@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("QA notify project", ownerTeamId: team.Id);
        await SeedProjectMemberAsync(project.Id, reviewer.Id);
        var task = await SeedTaskAsync("Needs QA", project.Id, assigneeId: owner.Id, requiresQa: true);
        var ownerClient = await AuthenticatedClientAsync("qa_notify_owner@pulse.io");

        var sendResponse = await ownerClient.PostAsync($"/api/v1/tasks/{task.Id}/send-to-qa", null);
        var sent = (await sendResponse.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;
        sent.QaTaskId.Should().NotBeNull();

        var reviewerClient = await AuthenticatedClientAsync("qa_notify_reviewer@pulse.io");
        var notifResponse = await reviewerClient.GetAsync("/api/v1/notifications");
        var notifications = (await notifResponse.Content.ReadFromJsonAsync<ApiResponse<PagedResult<NotificationDto>>>(JsonOpts))!.Data!;

        notifications.Items.Should().Contain(n => n.Kind == "task_assigned");
    }

    [Fact]
    public async Task SendToQa_finds_a_discipline_matched_reviewer_outside_the_project_and_grants_them_access()
    {
        var owner = await SeedEngineerAsync("qa_widen_owner@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("QA widen project");
        var task = await SeedTaskAsync("Needs QA", project.Id, assigneeId: owner.Id, requiresQa: true);

        var reviewer = await SeedEngineerAsync("qa_widen_reviewer@pulse.io", Roles.Engineer, isQa: true);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Pulse.Infrastructure.Persistence.PulseDbContext>();
            (await db.Tasks.FindAsync(task.Id))!.SetDiscipline(Discipline.Backend);
            (await db.Engineers.FindAsync(reviewer.Id))!.SetDiscipline(Discipline.Backend);
            await db.SaveChangesAsync();
        }

        var ownerClient = await AuthenticatedClientAsync("qa_widen_owner@pulse.io");
        var sendResponse = await ownerClient.PostAsync($"/api/v1/tasks/{task.Id}/send-to-qa", null);

        sendResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var sent = (await sendResponse.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;

        var reviewerClient = await AuthenticatedClientAsync("qa_widen_reviewer@pulse.io");
        var qaTaskResponse = await reviewerClient.GetAsync($"/api/v1/tasks/{sent.QaTaskId}");
        qaTaskResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "the reviewer should have been auto-added as a project member, not just assigned");
        var qaTask = (await qaTaskResponse.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;
        qaTask.AssigneeId.Should().Be(reviewer.Id);
    }

    [Fact]
    public async Task SendToQa_assigns_the_explicitly_chosen_reviewer_instead_of_the_auto_pick()
    {
        var owner = await SeedEngineerAsync("qa_pick_owner@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("QA explicit pick project");
        var task = await SeedTaskAsync("Needs QA", project.Id, assigneeId: owner.Id, requiresQa: true);

        // A discipline-less task restricts manual QA picks to department heads (QaAssignmentPolicy)
        // — not what this test is about, so give the task one. Only the task record is touched, not
        // any engineer's discipline, so this can't collide with the org-wide discipline-match tier
        // other tests rely on being uncontested.
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Pulse.Infrastructure.Persistence.PulseDbContext>();
            (await db.Tasks.FindAsync(task.Id))!.SetDiscipline(Discipline.Backend);
            await db.SaveChangesAsync();
        }

        // The only QA engineer on the project — would be the auto-pick's (tier 3) choice if no
        // override were sent. Deliberately not discipline-matched to the task (its own discipline
        // doesn't match Backend), so this doesn't touch the org-wide discipline-match tier either.
        var autoPick = await SeedEngineerAsync("qa_pick_auto@pulse.io", Roles.Engineer, isQa: true);
        await SeedProjectMemberAsync(project.Id, autoPick.Id);

        // Not on the project — only reachable via an explicit pick.
        var chosen = await SeedEngineerAsync("qa_pick_chosen@pulse.io", Roles.Engineer, isQa: true);

        var ownerClient = await AuthenticatedClientAsync("qa_pick_owner@pulse.io");
        var sendResponse = await ownerClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/send-to-qa", new { qaEngineerId = chosen.Id });

        sendResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var sent = (await sendResponse.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;

        var chosenClient = await AuthenticatedClientAsync("qa_pick_chosen@pulse.io");
        var qaTaskResponse = await chosenClient.GetAsync($"/api/v1/tasks/{sent.QaTaskId}");
        qaTaskResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "the explicitly chosen reviewer should have been auto-added as a project member");
        var qaTask = (await qaTaskResponse.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;
        qaTask.AssigneeId.Should().Be(chosen.Id, "the explicit pick must override the auto-assign recommendation");
    }

    [Fact]
    public async Task SendToQa_rejects_an_explicitly_chosen_reviewer_who_is_not_a_QA_engineer()
    {
        var owner = await SeedEngineerAsync("qa_pick_reject_owner@pulse.io", Roles.Engineer);
        var notQa = await SeedEngineerAsync("qa_pick_reject_notqa@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("QA explicit pick rejection project");
        var task = await SeedTaskAsync("Needs QA", project.Id, assigneeId: owner.Id, requiresQa: true);
        var client = await AuthenticatedClientAsync("qa_pick_reject_owner@pulse.io");

        var sendResponse = await client.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/send-to-qa", new { qaEngineerId = notQa.Id });

        sendResponse.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Original_assignee_can_accept_their_own_unassigned_QA_task()
    {
        var engineer = await SeedEngineerAsync("qa_self_accept@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("QA self-accept project");
        var task = await SeedTaskAsync("Needs QA", project.Id, assigneeId: engineer.Id, requiresQa: true);
        var client = await AuthenticatedClientAsync("qa_self_accept@pulse.io");

        var sendResponse = await client.PostAsync($"/api/v1/tasks/{task.Id}/send-to-qa", null);
        var sent = (await sendResponse.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;

        var acceptResponse = await client.PostAsync($"/api/v1/tasks/{sent.QaTaskId}/mark-done", null);

        acceptResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var originalResponse = await client.GetAsync($"/api/v1/tasks/{task.Id}");
        var original = (await originalResponse.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;
        original.Status.Should().Be("done");
    }

    [Fact]
    public async Task Unrelated_engineer_still_cannot_accept_someone_elses_unassigned_QA_task()
    {
        var owner = await SeedEngineerAsync("qa_owner@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("qa_outsider@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("QA outsider project");
        var task = await SeedTaskAsync("Needs QA", project.Id, assigneeId: owner.Id, requiresQa: true);
        var ownerClient = await AuthenticatedClientAsync("qa_owner@pulse.io");
        var sendResponse = await ownerClient.PostAsync($"/api/v1/tasks/{task.Id}/send-to-qa", null);
        var sent = (await sendResponse.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;

        var outsiderClient = await AuthenticatedClientAsync("qa_outsider@pulse.io");
        var response = await outsiderClient.PostAsync($"/api/v1/tasks/{sent.QaTaskId}/mark-done", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Department_head_of_the_QA_reviewer_can_reject_their_review()
    {
        var team = await SeedTeamAsync("QA reject dept team", department: "Engineering");
        var reviewer = await SeedEngineerAsync("qa_reject_reviewer@pulse.io", Roles.Engineer, isQa: true);
        await AssignEngineerToTeamAsync(reviewer.Id, team.Id);

        var owner = await SeedEngineerAsync("qa_reject_owner@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("QA reject dept project", ownerTeamId: team.Id);
        await SeedProjectMemberAsync(project.Id, reviewer.Id);
        var task = await SeedTaskAsync("Needs QA", project.Id, assigneeId: owner.Id, requiresQa: true);

        var ownerClient = await AuthenticatedClientAsync("qa_reject_owner@pulse.io");
        var sendResponse = await ownerClient.PostAsync($"/api/v1/tasks/{task.Id}/send-to-qa", null);
        var sent = (await sendResponse.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;
        sent.QaTaskId.Should().NotBeNull("a QA-flagged, discipline-less-but-project-member reviewer should auto-match");

        var deptHead = await SeedEngineerAsync("qa_reject_depthead@pulse.io", Roles.HeadOfRnD);
        await AssignEngineerToTeamAsync(deptHead.Id, team.Id);
        var deptHeadClient = await AuthenticatedClientAsync("qa_reject_depthead@pulse.io");

        // The frontend disables "Reject" based on this flag rather than only failing on click —
        // confirm it actually reflects what the API will allow.
        var qaTaskResponse = await deptHeadClient.GetAsync($"/api/v1/tasks/{sent.QaTaskId}");
        var qaTaskDto = (await qaTaskResponse.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;
        qaTaskDto.CanRejectQa.Should().BeTrue();

        var proposeResponse = await deptHeadClient.PostAsJsonAsync($"/api/v1/tasks/{sent.QaTaskId}/propose-qa-rejection", new { reason = "Needs rework" });

        proposeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var proposed = (await proposeResponse.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;
        proposed.PendingRejectionActorId.Should().Be(deptHead.Id);
        proposed.Status.Should().Be("inQa", "nothing moves until the rejection is confirmed");

        // The original task hasn't reactivated yet — just proposing a rejection isn't an iteration.
        var stillPending = await ownerClient.GetAsync($"/api/v1/tasks/{task.Id}");
        var stillPendingDto = (await stillPending.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;
        stillPendingDto.Status.Should().Be("inQa");
        stillPendingDto.PendingRejectionReason.Should().Be("Needs rework");

        var confirmResponse = await deptHeadClient.PostAsync($"/api/v1/tasks/{sent.QaTaskId}/confirm-qa-rejection", null);

        confirmResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var rejected = (await confirmResponse.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;
        rejected.ReactivatedByEngineerId.Should().Be(deptHead.Id);
        rejected.ReactivatedByName.Should().NotBeNullOrEmpty();

        // Confirm it round-trips through the database, not just the immediate response.
        var refetched = await ownerClient.GetAsync($"/api/v1/tasks/{task.Id}");
        var refetchedDto = (await refetched.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;
        refetchedDto.ReactivatedByEngineerId.Should().Be(deptHead.Id);
        refetchedDto.ReactivationReason.Should().Be("Needs rework");
        refetchedDto.PendingRejectionReason.Should().BeNull();
    }

    [Fact]
    public async Task Engineer_can_respond_and_QA_can_withdraw_with_no_iteration_counted()
    {
        var team = await SeedTeamAsync("QA withdraw dept team", department: "Engineering");
        var reviewer = await SeedEngineerAsync("qa_withdraw_reviewer@pulse.io", Roles.Engineer, isQa: true);
        await AssignEngineerToTeamAsync(reviewer.Id, team.Id);

        var owner = await SeedEngineerAsync("qa_withdraw_owner@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("QA withdraw project", ownerTeamId: team.Id);
        await SeedProjectMemberAsync(project.Id, reviewer.Id);
        var task = await SeedTaskAsync("Needs QA", project.Id, assigneeId: owner.Id, requiresQa: true);

        var ownerClient = await AuthenticatedClientAsync("qa_withdraw_owner@pulse.io");
        var sendResponse = await ownerClient.PostAsync($"/api/v1/tasks/{task.Id}/send-to-qa", null);
        var sent = (await sendResponse.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;

        var reviewerClient = await AuthenticatedClientAsync("qa_withdraw_reviewer@pulse.io");
        await reviewerClient.PostAsJsonAsync($"/api/v1/tasks/{sent.QaTaskId}/propose-qa-rejection", new { reason = "Fails on staging" });

        var respondResponse = await ownerClient.PostAsJsonAsync(
            $"/api/v1/tasks/{task.Id}/respond-to-qa-rejection", new { response = "That's an environment config issue, not the code." });
        respondResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var responded = (await respondResponse.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;
        responded.PendingRejectionResponse.Should().Be("That's an environment config issue, not the code.");

        var withdrawResponse = await reviewerClient.PostAsync($"/api/v1/tasks/{sent.QaTaskId}/withdraw-qa-rejection", null);
        withdrawResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var refetched = await ownerClient.GetAsync($"/api/v1/tasks/{task.Id}");
        var refetchedDto = (await refetched.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;
        refetchedDto.Status.Should().Be("inQa", "review continues — no iteration was counted");
        refetchedDto.PendingRejectionReason.Should().BeNull();
        refetchedDto.ReactivationReason.Should().BeNull();
    }

    [Fact]
    public async Task Unrelated_department_head_cannot_reject_someone_elses_QA_review()
    {
        var reviewerTeam = await SeedTeamAsync("QA reject reviewer team", department: "Engineering");
        var reviewer = await SeedEngineerAsync("qa_reject_r2@pulse.io", Roles.Engineer, isQa: true);
        await AssignEngineerToTeamAsync(reviewer.Id, reviewerTeam.Id);

        var owner = await SeedEngineerAsync("qa_reject_o2@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("QA reject unrelated project");
        await SeedProjectMemberAsync(project.Id, reviewer.Id);
        var task = await SeedTaskAsync("Needs QA", project.Id, assigneeId: owner.Id, requiresQa: true);

        var ownerClient = await AuthenticatedClientAsync("qa_reject_o2@pulse.io");
        var sendResponse = await ownerClient.PostAsync($"/api/v1/tasks/{task.Id}/send-to-qa", null);
        var sent = (await sendResponse.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;
        sent.QaTaskId.Should().NotBeNull();

        var otherTeam = await SeedTeamAsync("QA reject other team", department: "Design");
        var otherDeptHead = await SeedEngineerAsync("qa_reject_otherhead@pulse.io", Roles.HeadOfDesign);
        await AssignEngineerToTeamAsync(otherDeptHead.Id, otherTeam.Id);
        var otherClient = await AuthenticatedClientAsync("qa_reject_otherhead@pulse.io");

        // This head can't even view the task (different department, not a project member) —
        // a stronger guarantee for the frontend than a merely-disabled button.
        var qaTaskResponse = await otherClient.GetAsync($"/api/v1/tasks/{sent.QaTaskId}");
        qaTaskResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var response = await otherClient.PostAsJsonAsync($"/api/v1/tasks/{sent.QaTaskId}/propose-qa-rejection", new { reason = "Needs rework" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task MarkDone_rejects_a_task_that_requires_QA_and_was_never_sent()
    {
        var engineer = await SeedEngineerAsync("qa_bypass@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("QA bypass project");
        var task = await SeedTaskAsync("Needs QA", project.Id, assigneeId: engineer.Id, requiresQa: true);
        var client = await AuthenticatedClientAsync("qa_bypass@pulse.io");

        var response = await client.PostAsync($"/api/v1/tasks/{task.Id}/mark-done", null);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<object>>(JsonOpts);
        body!.Error!.Message.Should().Contain("QA");
    }

    [Fact]
    public async Task SendToQa_returns_400_when_the_target_is_itself_a_QA_task()
    {
        var engineer = await SeedEngineerAsync("qa_no_recursive@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("QA no-recursive-send project");
        await SeedProjectMemberAsync(project.Id, engineer.Id);
        var task = await SeedTaskAsync("Needs QA", project.Id, assigneeId: engineer.Id, requiresQa: true);
        var client = await AuthenticatedClientAsync("qa_no_recursive@pulse.io");
        var sendResponse = await client.PostAsync($"/api/v1/tasks/{task.Id}/send-to-qa", null);
        var sent = (await sendResponse.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;

        var response = await client.PostAsync($"/api/v1/tasks/{sent.QaTaskId}/send-to-qa", null);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task ConfirmQaRejection_flagged_Backend_auto_routes_the_task_back_to_the_backend_engineer()
    {
        var backendDev = await SeedEngineerAsync("qa_autoroute_backend@pulse.io", Roles.Engineer);
        var frontendDev = await SeedEngineerAsync("qa_autoroute_frontend@pulse.io", Roles.Engineer);
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Pulse.Infrastructure.Persistence.PulseDbContext>();
            (await db.Engineers.FindAsync(backendDev.Id))!.SetDiscipline(Discipline.Backend);
            (await db.Engineers.FindAsync(frontendDev.Id))!.SetDiscipline(Discipline.Frontend);
            await db.SaveChangesAsync();
        }
        var pmo = await SeedEngineerAsync("qa_autoroute_pmo@pulse.io", Roles.HeadOfPmo);
        var project = await SeedProjectAsync("QA auto-route project");
        await SeedProjectMemberAsync(project.Id, backendDev.Id);
        var backendClient = await AuthenticatedClientAsync("qa_autoroute_backend@pulse.io");

        var created = await backendClient.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Two-stage feature",
            points = 5,
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)).ToString("yyyy-MM-dd"),
            projectId = project.Id,
            assigneeId = backendDev.Id,
            requiresFrontendHandoff = true,
            requiresQa = true,
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var task = (await created.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;

        (await backendClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/hand-off-to-frontend",
            new { frontendAssigneeId = frontendDev.Id })).StatusCode.Should().Be(HttpStatusCode.OK);

        var frontendClient = await AuthenticatedClientAsync("qa_autoroute_frontend@pulse.io");
        var sendResponse = await frontendClient.PostAsync($"/api/v1/tasks/{task.Id}/send-to-qa", null);
        sendResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var sent = (await sendResponse.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;

        var pmoClient = await AuthenticatedClientAsync("qa_autoroute_pmo@pulse.io");
        var proposeResponse = await pmoClient.PostAsJsonAsync($"/api/v1/tasks/{sent.QaTaskId}/propose-qa-rejection",
            new { reason = "Actually a backend bug", targetStage = "backend" });
        proposeResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var confirmResponse = await pmoClient.PostAsync($"/api/v1/tasks/{sent.QaTaskId}/confirm-qa-rejection", null);
        confirmResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var confirmed = (await confirmResponse.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;

        confirmed.Status.Should().Be("active");
        confirmed.CurrentStage.Should().Be("backend");
        confirmed.AssigneeId.Should().Be(backendDev.Id);

        var backendNotifResponse = await backendClient.GetAsync("/api/v1/notifications");
        var backendNotifications = (await backendNotifResponse.Content.ReadFromJsonAsync<ApiResponse<PagedResult<NotificationDto>>>(JsonOpts))!.Data!;
        backendNotifications.Items.Should().Contain(n => n.Kind == "task_assigned");
    }
}
