using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Sprints;
using Pulse.Application.Sprints.Queries;
using Pulse.Domain.Engineers;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace Pulse.IntegrationTests.Sprints;

[Collection("Integration")]
public class SprintsTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public SprintsTests(PulseWebApplicationFactory factory) : base(factory) { }

    private static object SprintPayload(Guid projectId, string name) => new
    {
        projectId,
        name,
        goal = "Ship the feature",
        startDate = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"),
        endDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(14)).ToString("yyyy-MM-dd")
    };

    private async Task<Pulse.Domain.Projects.Project> SeedSprintableProjectAsync(string name, Guid? teamLeadId = null)
    {
        var team = await SeedTeamAsync($"{name} Team", teamLeadId);
        return await SeedProjectAsync(name, team.Id);
    }

    // ── access control ────────────────────────────────────────────────────────

    [Fact]
    public async Task Engineer_cannot_create_sprint()
    {
        var project = await SeedSprintableProjectAsync("Eng Denied Project");
        await SeedEngineerAsync("sprint_eng_denied@pulse.io", Roles.Engineer);
        var client = await AuthenticatedClientAsync("sprint_eng_denied@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/sprints", SprintPayload(project.Id, "Forbidden sprint"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Unauthenticated_cannot_list_sprints()
    {
        var response = await Client.GetAsync("/api/v1/sprints");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── create ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateSprint_returns_403_for_team_lead_role()
    {
        // Sprint creation is PMO/Functional/Product heads only — Team Lead used to be allowed
        // (TeamLeadOrAbove) but was deliberately narrowed to SprintCreatorOrAbove.
        var lead = await SeedEngineerAsync("sprint_lead_create_denied@pulse.io", Roles.TeamLead);
        var project = await SeedSprintableProjectAsync("Sprint Create Denied Project", lead.Id);
        var client = await AuthenticatedClientAsync("sprint_lead_create_denied@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/sprints",
            SprintPayload(project.Id, "Sprint Alpha"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CreateSprint_returns_201_for_head_of_pmo()
    {
        await SeedEngineerAsync("sprint_pmo_create@pulse.io", Roles.HeadOfPmo);
        var project = await SeedSprintableProjectAsync("Sprint Create PMO Project");
        var client = await AuthenticatedClientAsync("sprint_pmo_create@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/sprints",
            SprintPayload(project.Id, "Sprint Alpha"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<SprintDto>>(JsonOpts);
        body!.Data!.Name.Should().Be("Sprint Alpha");
        body.Data.Status.Should().Be("Planning");
    }

    [Fact]
    public async Task CreateSprint_returns_403_for_head_of_rnd()
    {
        // A department head who isn't PMO/Functional/Product (e.g. Head of R&D) is now excluded,
        // even though every head role used to satisfy the old TeamLeadOrAbove gate.
        await SeedEngineerAsync("sprint_rnd_create_denied@pulse.io", Roles.HeadOfRnD);
        var project = await SeedSprintableProjectAsync("Sprint Create RnD Denied Project");
        var client = await AuthenticatedClientAsync("sprint_rnd_create_denied@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/sprints",
            SprintPayload(project.Id, "Sprint Alpha"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CreateSprint_returns_400_when_endDate_before_startDate()
    {
        await SeedEngineerAsync("sprint_validation@pulse.io", Roles.HeadOfPmo);
        var project = await SeedSprintableProjectAsync("Validation Project");
        var client = await AuthenticatedClientAsync("sprint_validation@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/sprints", new
        {
            projectId = project.Id,
            name = "Bad dates",
            startDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(14)).ToString("yyyy-MM-dd"),
            endDate = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd")
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateSprint_sanitizes_goal_html()
    {
        await SeedEngineerAsync("sprint_goal_sanitize@pulse.io", Roles.HeadOfPmo);
        var project = await SeedSprintableProjectAsync("Goal Sanitize Project");
        var client = await AuthenticatedClientAsync("sprint_goal_sanitize@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/sprints", new
        {
            projectId = project.Id,
            name = "Formatted goal sprint",
            goal = "<p>Ship <strong>the</strong> feature</p><script>alert(1)</script>",
            startDate = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"),
            endDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(14)).ToString("yyyy-MM-dd")
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<SprintDto>>(JsonOpts);
        body!.Data!.Goal.Should().Contain("<strong>the</strong>");
        body.Data.Goal.Should().NotContain("<script>");
    }

    // ── get ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSprint_returns_200_with_sprint_data()
    {
        await SeedEngineerAsync("sprint_get_head@pulse.io", Roles.HeadOfPmo);
        var project = await SeedSprintableProjectAsync("Get Sprint Project");
        var client = await AuthenticatedClientAsync("sprint_get_head@pulse.io");

        var created = (await (await client.PostAsJsonAsync("/api/v1/sprints",
            SprintPayload(project.Id, "Sprint to fetch")))
            .Content.ReadFromJsonAsync<ApiResponse<SprintDto>>(JsonOpts))!.Data!;

        var getResp = await client.GetAsync($"/api/v1/sprints/{created.Id}");

        getResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await getResp.Content.ReadFromJsonAsync<ApiResponse<SprintDto>>(JsonOpts);
        body!.Data!.Id.Should().Be(created.Id);
    }

    [Fact]
    public async Task GetSprint_returns_404_for_unknown_id()
    {
        await SeedEngineerAsync("sprint_404@pulse.io", Roles.HeadOfRnD);
        var client = await AuthenticatedClientAsync("sprint_404@pulse.io");

        var response = await client.GetAsync($"/api/v1/sprints/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetSprint_returns_the_team_name_and_succeeds_for_the_team_lead()
    {
        // A team lead viewing their own sprint used to fail resolving the team's name — the
        // frontend called GET /teams/{id} separately, which is gated PmOrAbove and excludes
        // TeamLead entirely. TeamName is now denormalized onto SprintDto instead (see
        // GetSprintHandler), so this needs no separate, more restrictive call at all. Sprint
        // creation itself is PMO/Functional/Product heads only, so a separate account creates
        // it here — the team lead only needs to GET it, which stays ungated.
        var lead = await SeedEngineerAsync("sprint_get_teamname_lead@pulse.io", Roles.TeamLead);
        var project = await SeedSprintableProjectAsync("GetSprint TeamName Project", lead.Id);
        await SeedEngineerAsync("sprint_get_teamname_pmo@pulse.io", Roles.HeadOfPmo);
        var creatorClient = await AuthenticatedClientAsync("sprint_get_teamname_pmo@pulse.io");
        var leadClient = await AuthenticatedClientAsync("sprint_get_teamname_lead@pulse.io");

        var created = (await (await creatorClient.PostAsJsonAsync("/api/v1/sprints",
            SprintPayload(project.Id, "Sprint with team name")))
            .Content.ReadFromJsonAsync<ApiResponse<SprintDto>>(JsonOpts))!.Data!;

        var getResp = await leadClient.GetAsync($"/api/v1/sprints/{created.Id}");

        getResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await getResp.Content.ReadFromJsonAsync<ApiResponse<SprintDto>>(JsonOpts);
        body!.Data!.TeamName.Should().Be("GetSprint TeamName Project Team");
    }

    // ── due-date cascade ──────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateSprint_shifts_due_dates_of_tasks_in_it_when_end_date_moves()
    {
        await SeedEngineerAsync("sprint_reschedule@pulse.io", Roles.HeadOfPmo);
        var project = await SeedSprintableProjectAsync("Reschedule Project");
        var client = await AuthenticatedClientAsync("sprint_reschedule@pulse.io");

        var created = (await (await client.PostAsJsonAsync("/api/v1/sprints",
            SprintPayload(project.Id, "Sprint to reschedule")))
            .Content.ReadFromJsonAsync<ApiResponse<SprintDto>>(JsonOpts))!.Data!;

        var task = await SeedTaskAsync("Task in the sprint", project.Id, dueDaysFromNow: 10);
        await AssignTaskToSprintAsync(task.Id, created.Id);
        var originalDueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10));

        var newEndDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(20));
        var patchResp = await client.PatchAsJsonAsync($"/api/v1/sprints/{created.Id}",
            new { endDate = newEndDate.ToString("yyyy-MM-dd"), dueDateChangeReason = "Release moved a week" });
        patchResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var taskResp = await client.GetAsync($"/api/v1/tasks/{task.Id}");
        var taskBody = await taskResp.Content.ReadFromJsonAsync<ApiResponse<Pulse.Application.Tasks.TaskDto>>(JsonOpts);
        taskBody!.Data!.DueDate.Should().Be(originalDueDate.AddDays(6));
    }

    [Fact]
    public async Task UpdateSprint_does_not_shift_due_date_of_a_done_task()
    {
        await SeedEngineerAsync("sprint_reschedule_done@pulse.io", Roles.HeadOfPmo);
        var project = await SeedSprintableProjectAsync("Reschedule Done Project");
        var client = await AuthenticatedClientAsync("sprint_reschedule_done@pulse.io");

        var created = (await (await client.PostAsJsonAsync("/api/v1/sprints",
            SprintPayload(project.Id, "Sprint with a done task")))
            .Content.ReadFromJsonAsync<ApiResponse<SprintDto>>(JsonOpts))!.Data!;

        var task = await SeedTaskAsync("Already-done task", project.Id, dueDaysFromNow: 10);
        await AssignTaskToSprintAsync(task.Id, created.Id);
        var originalDueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10));
        await MarkTaskDoneAsync(task.Id);

        var newEndDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(20));
        await client.PatchAsJsonAsync($"/api/v1/sprints/{created.Id}",
            new { endDate = newEndDate.ToString("yyyy-MM-dd") });

        var taskResp = await client.GetAsync($"/api/v1/tasks/{task.Id}");
        var taskBody = await taskResp.Content.ReadFromJsonAsync<ApiResponse<Pulse.Application.Tasks.TaskDto>>(JsonOpts);
        taskBody!.Data!.DueDate.Should().Be(originalDueDate);
    }

    // ── status transitions ────────────────────────────────────────────────────

    [Fact]
    public async Task ActivateSprint_transitions_Planning_to_Active()
    {
        await SeedEngineerAsync("sprint_activate@pulse.io", Roles.HeadOfPmo);
        var project = await SeedSprintableProjectAsync("Activate Project");
        var client = await AuthenticatedClientAsync("sprint_activate@pulse.io");

        var created = (await (await client.PostAsJsonAsync("/api/v1/sprints",
            SprintPayload(project.Id, "Sprint to activate")))
            .Content.ReadFromJsonAsync<ApiResponse<SprintDto>>(JsonOpts))!.Data!;

        var patchResp = await client.PatchAsJsonAsync($"/api/v1/sprints/{created.Id}",
            new { activate = true });

        patchResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await patchResp.Content.ReadFromJsonAsync<ApiResponse<SprintDto>>(JsonOpts);
        body!.Data!.Status.Should().Be("Active");
    }

    [Fact]
    public async Task CompleteSprint_transitions_Active_to_Completed()
    {
        await SeedEngineerAsync("sprint_complete@pulse.io", Roles.HeadOfPmo);
        var project = await SeedSprintableProjectAsync("Complete Project");
        var client = await AuthenticatedClientAsync("sprint_complete@pulse.io");

        var created = (await (await client.PostAsJsonAsync("/api/v1/sprints",
            SprintPayload(project.Id, "Sprint to complete")))
            .Content.ReadFromJsonAsync<ApiResponse<SprintDto>>(JsonOpts))!.Data!;

        await client.PatchAsJsonAsync($"/api/v1/sprints/{created.Id}", new { activate = true });
        var patchResp = await client.PatchAsJsonAsync($"/api/v1/sprints/{created.Id}",
            new { complete = true });

        patchResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await patchResp.Content.ReadFromJsonAsync<ApiResponse<SprintDto>>(JsonOpts);
        body!.Data!.Status.Should().Be("Completed");
    }

    [Fact]
    public async Task ActivateSprint_returns_422_when_sprint_is_already_active()
    {
        await SeedEngineerAsync("sprint_double_activate@pulse.io", Roles.HeadOfPmo);
        var project = await SeedSprintableProjectAsync("Double Activate Project");
        var client = await AuthenticatedClientAsync("sprint_double_activate@pulse.io");

        var created = (await (await client.PostAsJsonAsync("/api/v1/sprints",
            SprintPayload(project.Id, "Already active")))
            .Content.ReadFromJsonAsync<ApiResponse<SprintDto>>(JsonOpts))!.Data!;

        await client.PatchAsJsonAsync($"/api/v1/sprints/{created.Id}", new { activate = true });
        var second = await client.PatchAsJsonAsync($"/api/v1/sprints/{created.Id}", new { activate = true });

        second.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    // ── delete ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteSprint_returns_204_for_planning_sprint()
    {
        await SeedEngineerAsync("sprint_del_head@pulse.io", Roles.HeadOfPmo);
        var project = await SeedSprintableProjectAsync("Delete Sprint Project");
        var client = await AuthenticatedClientAsync("sprint_del_head@pulse.io");

        var created = (await (await client.PostAsJsonAsync("/api/v1/sprints",
            SprintPayload(project.Id, "Sprint to delete")))
            .Content.ReadFromJsonAsync<ApiResponse<SprintDto>>(JsonOpts))!.Data!;

        var deleteResp = await client.DeleteAsync($"/api/v1/sprints/{created.Id}");

        deleteResp.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeleteSprint_returns_422_for_active_sprint()
    {
        await SeedEngineerAsync("sprint_del_active_head@pulse.io", Roles.HeadOfPmo);
        var project = await SeedSprintableProjectAsync("Active Sprint Delete Project");
        var client = await AuthenticatedClientAsync("sprint_del_active_head@pulse.io");

        var created = (await (await client.PostAsJsonAsync("/api/v1/sprints",
            SprintPayload(project.Id, "Active sprint")))
            .Content.ReadFromJsonAsync<ApiResponse<SprintDto>>(JsonOpts))!.Data!;

        await client.PatchAsJsonAsync($"/api/v1/sprints/{created.Id}", new { activate = true });

        var deleteResp = await client.DeleteAsync($"/api/v1/sprints/{created.Id}");

        deleteResp.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task DeleteSprint_returns_403_for_team_lead_role()
    {
        var lead = await SeedEngineerAsync("sprint_del_lead_denied@pulse.io", Roles.TeamLead);
        var project = await SeedSprintableProjectAsync("Team Lead Delete Denied", lead.Id);
        var headSeed = await SeedEngineerAsync("sprint_del_head_seed@pulse.io", Roles.HeadOfPmo);
        var headClient = await AuthenticatedClientAsync("sprint_del_head_seed@pulse.io");
        var leadClient = await AuthenticatedClientAsync("sprint_del_lead_denied@pulse.io");

        var created = (await (await headClient.PostAsJsonAsync("/api/v1/sprints",
            SprintPayload(project.Id, "Lead cannot delete")))
            .Content.ReadFromJsonAsync<ApiResponse<SprintDto>>(JsonOpts))!.Data!;

        var deleteResp = await leadClient.DeleteAsync($"/api/v1/sprints/{created.Id}");

        deleteResp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── velocity ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetVelocity_returns_200_with_zero_points_for_empty_sprint()
    {
        await SeedEngineerAsync("sprint_velocity@pulse.io", Roles.HeadOfPmo);
        var project = await SeedSprintableProjectAsync("Velocity Project");
        var client = await AuthenticatedClientAsync("sprint_velocity@pulse.io");

        var created = (await (await client.PostAsJsonAsync("/api/v1/sprints",
            SprintPayload(project.Id, "Empty velocity sprint")))
            .Content.ReadFromJsonAsync<ApiResponse<SprintDto>>(JsonOpts))!.Data!;

        var velResp = await client.GetAsync($"/api/v1/sprints/{created.Id}/velocity");

        velResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await velResp.Content.ReadFromJsonAsync<ApiResponse<SprintVelocityDto>>(JsonOpts);
        body!.Data!.PlannedPoints.Should().Be(0);
        body.Data.DeliveredPoints.Should().Be(0);
    }

    [Fact]
    public async Task GetVelocity_does_not_double_count_a_task_that_went_through_QA()
    {
        var engineer = await SeedEngineerAsync("sprint_velocity_qa@pulse.io", Roles.HeadOfRnD);
        var project = await SeedSprintableProjectAsync("Velocity QA Project");
        var client = await AuthenticatedClientAsync("sprint_velocity_qa@pulse.io");
        await SeedEngineerAsync("sprint_velocity_qa_pmo@pulse.io", Roles.HeadOfPmo);
        var creatorClient = await AuthenticatedClientAsync("sprint_velocity_qa_pmo@pulse.io");

        var sprint = (await (await creatorClient.PostAsJsonAsync("/api/v1/sprints",
            SprintPayload(project.Id, "QA velocity sprint")))
            .Content.ReadFromJsonAsync<ApiResponse<SprintDto>>(JsonOpts))!.Data!;

        var task = await SeedTaskAsync("Needs QA", project.Id, points: 5, assigneeId: engineer.Id, requiresQa: true);
        await AssignTaskToSprintAsync(task.Id, sprint.Id);

        var sendResp = await client.PostAsync($"/api/v1/tasks/{task.Id}/send-to-qa", null);
        sendResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var sent = (await sendResp.Content.ReadFromJsonAsync<ApiResponse<Pulse.Application.Tasks.TaskDto>>(JsonOpts))!.Data!;
        sent.QaTaskId.Should().NotBeNull("no QA-flagged engineer exists on this project, so it auto-lands unassigned");

        // Original assignee self-accepts their own unassigned QA task.
        var acceptResp = await client.PostAsync($"/api/v1/tasks/{sent.QaTaskId}/mark-done", null);
        acceptResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var velResp = await client.GetAsync($"/api/v1/sprints/{sprint.Id}/velocity");
        var body = await velResp.Content.ReadFromJsonAsync<ApiResponse<SprintVelocityDto>>(JsonOpts);

        // Both the original task and its auto-created "[QA] ..." sub-task ended up in this same
        // sprint with 5 points each — planned/delivered must reflect one 5-point feature, not 10.
        body!.Data!.PlannedPoints.Should().Be(5);
        body.Data.DeliveredPoints.Should().Be(5);

        // Same story for task counts: 2 rows exist in the sprint (the feature + its QA sub-task),
        // but that's one unit of planned work, now done (accepting QA cascades Done to the parent).
        body.Data.TotalTasks.Should().Be(1);
        body.Data.DoneTasks.Should().Be(1);
    }

    [Fact]
    public async Task GetBurndown_does_not_double_count_a_task_that_went_through_QA()
    {
        var engineer = await SeedEngineerAsync("sprint_burndown_qa@pulse.io", Roles.HeadOfRnD);
        var project = await SeedSprintableProjectAsync("Burndown QA Project");
        var client = await AuthenticatedClientAsync("sprint_burndown_qa@pulse.io");
        await SeedEngineerAsync("sprint_burndown_qa_pmo@pulse.io", Roles.HeadOfPmo);
        var creatorClient = await AuthenticatedClientAsync("sprint_burndown_qa_pmo@pulse.io");

        // Starts today, so the burndown loop produces exactly one point (elapsed = 0), where
        // Ideal == total scope points.
        var sprint = (await (await creatorClient.PostAsJsonAsync("/api/v1/sprints",
            SprintPayload(project.Id, "QA burndown sprint")))
            .Content.ReadFromJsonAsync<ApiResponse<SprintDto>>(JsonOpts))!.Data!;

        var task = await SeedTaskAsync("Needs QA", project.Id, points: 5, assigneeId: engineer.Id, requiresQa: true);
        await AssignTaskToSprintAsync(task.Id, sprint.Id);

        var sendResp = await client.PostAsync($"/api/v1/tasks/{task.Id}/send-to-qa", null);
        var sent = (await sendResp.Content.ReadFromJsonAsync<ApiResponse<Pulse.Application.Tasks.TaskDto>>(JsonOpts))!.Data!;

        var burndownResp = await client.GetAsync($"/api/v1/sprints/{sprint.Id}/burndown");
        burndownResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var points = (await burndownResp.Content.ReadFromJsonAsync<ApiResponse<List<BurndownPointDto>>>(JsonOpts))!.Data!;

        // The original task (5 pts) and its "[QA] ..." sub-task (5 pts) both sit in this sprint —
        // total scope must reflect one 5-point feature, not 10.
        points.Should().ContainSingle();
        points[0].Ideal.Should().Be(5);
    }

    [Fact]
    public async Task GetBurndown_includes_paused_task_points_in_total_scope()
    {
        var engineer = await SeedEngineerAsync("sprint_burndown_paused@pulse.io", Roles.HeadOfRnD);
        var project = await SeedSprintableProjectAsync("Burndown Paused Project");
        var client = await AuthenticatedClientAsync("sprint_burndown_paused@pulse.io");
        await SeedEngineerAsync("sprint_burndown_paused_pmo@pulse.io", Roles.HeadOfPmo);
        var creatorClient = await AuthenticatedClientAsync("sprint_burndown_paused_pmo@pulse.io");

        var sprint = (await (await creatorClient.PostAsJsonAsync("/api/v1/sprints",
            SprintPayload(project.Id, "Paused burndown sprint")))
            .Content.ReadFromJsonAsync<ApiResponse<SprintDto>>(JsonOpts))!.Data!;

        var active = await SeedTaskAsync("Still active", project.Id, points: 5, assigneeId: engineer.Id);
        await AssignTaskToSprintAsync(active.Id, sprint.Id);
        var paused = await SeedTaskAsync("On hold", project.Id, points: 3, assigneeId: engineer.Id);
        await AssignTaskToSprintAsync(paused.Id, sprint.Id);

        var pauseResp = await client.PostAsJsonAsync($"/api/v1/tasks/{paused.Id}/pause", new { note = (string?)null });
        pauseResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var velResp = await client.GetAsync($"/api/v1/sprints/{sprint.Id}/velocity");
        var vel = (await velResp.Content.ReadFromJsonAsync<ApiResponse<SprintVelocityDto>>(JsonOpts))!.Data!;
        vel.PlannedPoints.Should().Be(8, "Planned pts already counts paused tasks as committed scope");

        var burndownResp = await client.GetAsync($"/api/v1/sprints/{sprint.Id}/burndown");
        var points = (await burndownResp.Content.ReadFromJsonAsync<ApiResponse<List<BurndownPointDto>>>(JsonOpts))!.Data!;

        // Burndown's total scope must match Planned pts — a paused task is still committed work,
        // just on hold, so it stays in the denominator the "Ideal" line is drawn against.
        points.Should().ContainSingle();
        points[0].Ideal.Should().Be(8);
    }
}
