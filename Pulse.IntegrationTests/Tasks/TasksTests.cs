using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Notifications;
using Pulse.Application.Overwork;
using Pulse.Application.Tasks;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Tasks;

[Collection("Integration")]
public class TasksTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public TasksTests(PulseWebApplicationFactory factory) : base(factory) { }

    // ── create ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateTask_returns_201_for_pm_role()
    {
        await SeedEngineerAsync("task_pm@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("CreateTask project");
        var client = await AuthenticatedClientAsync("task_pm@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "My task",
            points = 3,
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)).ToString("yyyy-MM-dd"),
            projectId = project.Id
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        body!.Data!.Title.Should().Be("My task");
        body.Data.Points.Should().Be(3);
    }

    [Fact]
    public async Task CreateTask_allows_omitting_points_and_creates_it_ungroomed()
    {
        await SeedEngineerAsync("task_no_points_pm@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("CreateTask no points project");
        var client = await AuthenticatedClientAsync("task_no_points_pm@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Ungroomed task",
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)).ToString("yyyy-MM-dd"),
            projectId = project.Id
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        body!.Data!.Points.Should().Be(0);
        body.Data.Status.Should().Be("backlog");
    }

    [Fact]
    public async Task CreateTask_returns_400_when_points_is_out_of_range()
    {
        await SeedEngineerAsync("task_bad_points_pm@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("CreateTask bad points project");
        var client = await AuthenticatedClientAsync("task_bad_points_pm@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Bad points task",
            points = 0,
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)).ToString("yyyy-MM-dd"),
            projectId = project.Id
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateTask_accepts_an_optional_priority()
    {
        await SeedEngineerAsync("task_priority_pm@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("CreateTask priority project");
        var client = await AuthenticatedClientAsync("task_priority_pm@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Prioritized task",
            points = 3,
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)).ToString("yyyy-MM-dd"),
            projectId = project.Id,
            priority = 5
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        body!.Data!.Priority.Should().Be(5);
    }

    [Fact]
    public async Task CreateTask_accepts_and_sanitizes_acceptance_criteria()
    {
        await SeedEngineerAsync("task_ac_pm@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("CreateTask acceptance criteria project");
        var client = await AuthenticatedClientAsync("task_ac_pm@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Task with acceptance criteria",
            points = 3,
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)).ToString("yyyy-MM-dd"),
            projectId = project.Id,
            acceptanceCriteria = "<p>Must work</p><script>alert(1)</script>"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        body!.Data!.AcceptanceCriteria.Should().Contain("<p>Must work</p>");
        body.Data.AcceptanceCriteria.Should().NotContain("<script>");
    }

    [Fact]
    public async Task CreateTask_returns_400_when_priority_is_out_of_range()
    {
        await SeedEngineerAsync("task_priority_bad_pm@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("CreateTask bad priority project");
        var client = await AuthenticatedClientAsync("task_priority_bad_pm@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Bad priority task",
            points = 3,
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)).ToString("yyyy-MM-dd"),
            projectId = project.Id,
            priority = 6
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateTask_with_an_assignee_notifies_them_in_app()
    {
        var assignee = await SeedEngineerAsync("create_notify_assignee@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("create_notify_pm@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("Create-with-assignee notify project");
        var pmClient = await AuthenticatedClientAsync("create_notify_pm@pulse.io");

        var response = await pmClient.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Assigned at creation",
            points = 3,
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)).ToString("yyyy-MM-dd"),
            projectId = project.Id,
            assigneeId = assignee.Id
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var assigneeClient = await AuthenticatedClientAsync("create_notify_assignee@pulse.io");
        var notifResponse = await assigneeClient.GetAsync("/api/v1/notifications");
        var notifications = (await notifResponse.Content.ReadFromJsonAsync<ApiResponse<PagedResult<NotificationDto>>>(JsonOpts))!.Data!;

        notifications.Items.Should().Contain(n => n.Kind == "task_assigned");
    }

    [Fact]
    public async Task CreateTask_as_an_engineer_targeting_someone_else_is_silently_corrected_to_self()
    {
        // The create-page UI already locks the assignee picker to "Assign to me" for this role
        // tier — this proves the backend enforces it too, not just the UI.
        var engineer = await SeedEngineerAsync("create_self_assign_eng@pulse.io", Roles.Engineer);
        var someoneElse = await SeedEngineerAsync("create_self_assign_other@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("Create self-assign project");
        await SeedProjectMemberAsync(project.Id, engineer.Id);
        var client = await AuthenticatedClientAsync("create_self_assign_eng@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Should land on me, not them",
            points = 3,
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)).ToString("yyyy-MM-dd"),
            projectId = project.Id,
            assigneeId = someoneElse.Id,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        body!.Data!.AssigneeId.Should().Be(engineer.Id);
    }

    [Fact]
    public async Task CreateTask_with_no_due_date_stores_null_not_year_one()
    {
        // Regression guard: CreateTaskRequest.DueDate used to be a non-nullable DateOnly, so
        // omitting it from the request body (the "New Task" form leaves it blank if the user
        // doesn't pick one) silently bound to default(DateOnly) = 0001-01-01 instead of null.
        // No points/assignee/priority here — a due date only becomes mandatory once one of
        // those gives the task enough shape to be scheduled (see CreateTask_requires_due_date_*).
        await SeedEngineerAsync("task_no_due_date@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("No Due Date project");
        var client = await AuthenticatedClientAsync("task_no_due_date@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "No due date task",
            projectId = project.Id,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        body!.Data!.DueDate.Should().BeNull();
    }

    [Fact]
    public async Task CreateTask_requires_due_date_once_points_are_set()
    {
        await SeedEngineerAsync("task_points_no_due_date@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("Points No Due Date project");
        var client = await AuthenticatedClientAsync("task_points_no_due_date@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Pointed but unscheduled",
            points = 3,
            projectId = project.Id,
        });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task CreateTask_requires_due_date_once_priority_is_set()
    {
        await SeedEngineerAsync("task_priority_no_due_date@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("Priority No Due Date project");
        var client = await AuthenticatedClientAsync("task_priority_no_due_date@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Prioritized but unscheduled",
            priority = 3,
            projectId = project.Id,
        });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task CreateTask_records_the_creating_actor()
    {
        var pm = await SeedEngineerAsync("task_creator_pm@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("Creator-tracking project");
        var client = await AuthenticatedClientAsync("task_creator_pm@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Track my creator",
            points = 3,
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)).ToString("yyyy-MM-dd"),
            projectId = project.Id
        });

        var body = await response.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        body!.Data!.CreatedById.Should().Be(pm.Id);
        body.Data.CreatorName.Should().Be(pm.Name);
    }

    [Fact]
    public async Task CreateTask_returns_403_for_engineer_without_project_access()
    {
        await SeedEngineerAsync("task_eng_denied@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("Denied project");
        var client = await AuthenticatedClientAsync("task_eng_denied@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Should fail",
            points = 1,
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)).ToString("yyyy-MM-dd"),
            projectId = project.Id
        });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CreateTask_returns_201_for_engineer_who_is_a_project_member()
    {
        var engineer = await SeedEngineerAsync("task_eng_member@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("Member-accessible project");
        await SeedProjectMemberAsync(project.Id, engineer.Id);
        var client = await AuthenticatedClientAsync("task_eng_member@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Self-service task",
            points = 2,
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)).ToString("yyyy-MM-dd"),
            projectId = project.Id
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        body!.Data!.Title.Should().Be("Self-service task");
    }

    [Fact]
    public async Task CreateTask_returns_400_when_required_fields_missing()
    {
        await SeedEngineerAsync("task_validation@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("task_validation@pulse.io");

        var response = await client.PostAsJsonAsync("/api/v1/tasks", new { points = 3 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── get ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetTask_returns_200_with_task_data()
    {
        await SeedEngineerAsync("task_get_pm@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("GetTask project");
        var client = await AuthenticatedClientAsync("task_get_pm@pulse.io");

        var createResp = await client.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Fetch me",
            points = 5,
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)).ToString("yyyy-MM-dd"),
            projectId = project.Id
        });
        var created = (await createResp.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;

        var getResp = await client.GetAsync($"/api/v1/tasks/{created.Id}");

        getResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await getResp.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        body!.Data!.Id.Should().Be(created.Id);
        body.Data.Title.Should().Be("Fetch me");
    }

    [Fact]
    public async Task GetTask_returns_404_for_unknown_id()
    {
        await SeedEngineerAsync("task_404@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("task_404@pulse.io");

        var response = await client.GetAsync($"/api/v1/tasks/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── update ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateTask_changes_title()
    {
        await SeedEngineerAsync("task_update@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("UpdateTask project");
        var client = await AuthenticatedClientAsync("task_update@pulse.io");

        var created = (await (await client.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Original",
            points = 2,
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)).ToString("yyyy-MM-dd"),
            projectId = project.Id
        })).Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;

        var patchResp = await client.PatchAsJsonAsync($"/api/v1/tasks/{created.Id}", new { title = "Updated" });

        patchResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await patchResp.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        body!.Data!.Title.Should().Be("Updated");
    }

    [Fact]
    public async Task UpdateTask_sets_priority()
    {
        await SeedEngineerAsync("task_update_priority@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("UpdateTask priority project");
        var client = await AuthenticatedClientAsync("task_update_priority@pulse.io");

        var created = (await (await client.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Needs priority",
            points = 2,
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)).ToString("yyyy-MM-dd"),
            projectId = project.Id
        })).Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;
        created.Priority.Should().BeNull();

        var patchResp = await client.PatchAsJsonAsync($"/api/v1/tasks/{created.Id}", new { priority = 4 });

        patchResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await patchResp.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        body!.Data!.Priority.Should().Be(4);
    }

    [Fact]
    public async Task UpdateTask_clears_priority_when_removePriority_is_set()
    {
        // Priority is a plain int?, so a PATCH with no `priority` field is indistinguishable from
        // "leave it alone" — removePriority is the explicit signal to actually clear it, the same
        // pattern removeFromEpic/removeFromSprint already use for their own nullable fields.
        await SeedEngineerAsync("task_clear_priority@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("UpdateTask clear priority project");
        var client = await AuthenticatedClientAsync("task_clear_priority@pulse.io");

        var created = (await (await client.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Has a priority",
            points = 2,
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)).ToString("yyyy-MM-dd"),
            projectId = project.Id,
            priority = 3
        })).Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;
        created.Priority.Should().Be(3);

        var patchResp = await client.PatchAsJsonAsync($"/api/v1/tasks/{created.Id}", new { removePriority = true });

        patchResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await patchResp.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        body!.Data!.Priority.Should().BeNull();
    }

    [Fact]
    public async Task UpdateTask_returns_400_when_priority_is_out_of_range()
    {
        await SeedEngineerAsync("task_update_bad_priority@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("UpdateTask bad priority project");
        var client = await AuthenticatedClientAsync("task_update_bad_priority@pulse.io");

        var created = (await (await client.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Bad priority update",
            points = 2,
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)).ToString("yyyy-MM-dd"),
            projectId = project.Id
        })).Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;

        var patchResp = await client.PatchAsJsonAsync($"/api/v1/tasks/{created.Id}", new { priority = 0 });

        patchResp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateTask_requires_due_date_once_priority_is_set_on_a_bare_task()
    {
        // Mirrors CreateTask's rule (see CreateTask_requires_due_date_once_priority_is_set) —
        // editing an existing unscheduled task to add a priority must not bypass it.
        await SeedEngineerAsync("task_update_priority_no_due_date@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("Update Priority No Due Date project");
        var client = await AuthenticatedClientAsync("task_update_priority_no_due_date@pulse.io");

        var created = (await (await client.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Bare backlog item",
            projectId = project.Id
        })).Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;
        created.DueDate.Should().BeNull();

        var patchResp = await client.PatchAsJsonAsync($"/api/v1/tasks/{created.Id}", new { priority = 3 });

        patchResp.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task UpdateTask_requires_due_date_once_points_are_set_on_a_bare_task()
    {
        await SeedEngineerAsync("task_update_points_no_due_date@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("Update Points No Due Date project");
        var client = await AuthenticatedClientAsync("task_update_points_no_due_date@pulse.io");

        var created = (await (await client.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Bare backlog item",
            projectId = project.Id
        })).Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;

        var patchResp = await client.PatchAsJsonAsync($"/api/v1/tasks/{created.Id}", new { points = 5 });

        patchResp.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task UpdateTask_allows_editing_a_bare_task_without_touching_points_assignee_or_priority()
    {
        // Unrelated edits to an already-noncompliant/ungroomed task (no due date, no points, no
        // priority, no assignee) must not be retroactively blocked — only a call that's actually
        // setting one of those three fields should ever require a due date.
        await SeedEngineerAsync("task_update_unrelated_field@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("Update Unrelated Field project");
        var client = await AuthenticatedClientAsync("task_update_unrelated_field@pulse.io");

        var created = (await (await client.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Bare backlog item",
            projectId = project.Id
        })).Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;

        var patchResp = await client.PatchAsJsonAsync($"/api/v1/tasks/{created.Id}", new { description = "still ungroomed" });

        patchResp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task MarkTaskDone_transitions_status_to_Done()
    {
        await SeedEngineerAsync("task_done_pm@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("MarkDone project");
        var client = await AuthenticatedClientAsync("task_done_pm@pulse.io");

        var created = (await (await client.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Finish me",
            points = 1,
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)).ToString("yyyy-MM-dd"),
            projectId = project.Id
        })).Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;

        var patchResp = await client.PatchAsJsonAsync($"/api/v1/tasks/{created.Id}", new { markDone = true });

        patchResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await patchResp.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        body!.Data!.Status.Should().Be("done");
    }

    // ── delete ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteTask_returns_204()
    {
        await SeedEngineerAsync("task_del_pm@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("DeleteTask project");
        var client = await AuthenticatedClientAsync("task_del_pm@pulse.io");

        var created = (await (await client.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Delete me",
            points = 1,
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)).ToString("yyyy-MM-dd"),
            projectId = project.Id
        })).Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;

        var deleteResp = await client.DeleteAsync($"/api/v1/tasks/{created.Id}");

        deleteResp.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeleteTask_returns_404_for_unknown_id()
    {
        await SeedEngineerAsync("task_del_404@pulse.io", Roles.ProjectManager);
        var client = await AuthenticatedClientAsync("task_del_404@pulse.io");

        var response = await client.DeleteAsync($"/api/v1/tasks/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── blocker ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task FlagBlocker_returns_200_with_blocker_reason()
    {
        var assignee = await SeedEngineerAsync("task_blocker_eng@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("task_blocker_pm@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("Blocker project");
        var pmClient = await AuthenticatedClientAsync("task_blocker_pm@pulse.io");
        var engClient = await AuthenticatedClientAsync("task_blocker_eng@pulse.io");

        var created = (await (await pmClient.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Blocked task",
            points = 2,
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)).ToString("yyyy-MM-dd"),
            projectId = project.Id,
            assigneeId = assignee.Id
        })).Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;

        var flagResp = await engClient.PostAsJsonAsync(
            $"/api/v1/tasks/{created.Id}/blocker",
            new { reason = "Waiting for API credentials" });

        flagResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await flagResp.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        body!.Data!.BlockerReason.Should().Be("Waiting for API credentials");
        body.Data.Status.Should().Be("blocked");
    }

    [Fact]
    public async Task FlagBlocker_returns_403_for_non_assignee()
    {
        var assignee = await SeedEngineerAsync("task_blk_assignee@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("task_blk_other@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("task_blk_pm@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("Blocker 403 project");
        var pmClient = await AuthenticatedClientAsync("task_blk_pm@pulse.io");
        var otherClient = await AuthenticatedClientAsync("task_blk_other@pulse.io");

        var created = (await (await pmClient.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Others task",
            points = 1,
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(4)).ToString("yyyy-MM-dd"),
            projectId = project.Id,
            assigneeId = assignee.Id
        })).Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;

        var flagResp = await otherClient.PostAsJsonAsync(
            $"/api/v1/tasks/{created.Id}/blocker",
            new { reason = "Sneaky blocker" });

        flagResp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task FlagBlocker_allows_PMO_to_flag_a_blocker_on_an_engineers_task()
    {
        var assignee = await SeedEngineerAsync("task_blk_pmo_eng@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("task_blk_pmo_head@pulse.io", Roles.HeadOfPmo);
        var project = await SeedProjectAsync("Blocker PMO project");
        var pmoClient = await AuthenticatedClientAsync("task_blk_pmo_head@pulse.io");

        var created = (await (await pmoClient.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Engineer's task",
            points = 2,
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)).ToString("yyyy-MM-dd"),
            projectId = project.Id,
            assigneeId = assignee.Id
        })).Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;

        var flagResp = await pmoClient.PostAsJsonAsync(
            $"/api/v1/tasks/{created.Id}/blocker",
            new { reason = "Vendor dependency isn't ready" });

        flagResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await flagResp.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        body!.Data!.Status.Should().Be("blocked");
    }

    [Fact]
    public async Task ClearBlocker_returns_task_to_Active_status()
    {
        var assignee = await SeedEngineerAsync("task_clr_eng@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("task_clr_pm@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("ClearBlocker project");
        var pmClient = await AuthenticatedClientAsync("task_clr_pm@pulse.io");
        var engClient = await AuthenticatedClientAsync("task_clr_eng@pulse.io");

        var created = (await (await pmClient.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Will be cleared",
            points = 2,
            dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(6)).ToString("yyyy-MM-dd"),
            projectId = project.Id,
            assigneeId = assignee.Id
        })).Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;

        await engClient.PostAsJsonAsync($"/api/v1/tasks/{created.Id}/blocker", new { reason = "Blocked" });
        var clearResp = await engClient.DeleteAsync($"/api/v1/tasks/{created.Id}/blocker");

        clearResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await clearResp.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        body!.Data!.Status.Should().Be("active");
        body.Data.BlockerReason.Should().BeNull();
    }

    // ── list ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListTasks_returns_200_for_authenticated_user()
    {
        await SeedEngineerAsync("task_list@pulse.io", Roles.Engineer);
        var client = await AuthenticatedClientAsync("task_list@pulse.io");

        var response = await client.GetAsync("/api/v1/tasks");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ListTasks_returns_401_for_unauthenticated()
    {
        var response = await Client.GetAsync("/api/v1/tasks");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ListTasks_with_discipline_filter_excludes_other_disciplines_and_undisciplined_tasks()
    {
        // Reported bug: searching Discipline=Product returned tasks with no discipline set at all
        // (a prior "null also passes" rule meant to keep generic chores visible on an engineer's
        // own filtered board) — in practice most tasks have no discipline set, so the filter was
        // effectively showing almost everything regardless of what was picked.
        var engineer = await SeedEngineerAsync("task_discipline_filter@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("Discipline filter project");
        var productTask = await SeedTaskAsync("Product work", project.Id, assigneeId: engineer.Id);
        var backendTask = await SeedTaskAsync("Backend work", project.Id, assigneeId: engineer.Id);
        var undisciplinedTask = await SeedTaskAsync("No discipline set", project.Id, assigneeId: engineer.Id);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            (await db.Tasks.FindAsync(productTask.Id))!.SetDiscipline(Discipline.Product);
            (await db.Tasks.FindAsync(backendTask.Id))!.SetDiscipline(Discipline.Backend);
            await db.SaveChangesAsync();
        }

        var client = await AuthenticatedClientAsync("task_discipline_filter@pulse.io");
        var response = await client.GetAsync($"/api/v1/tasks?assigneeId={engineer.Id}&discipline=product");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<PagedResult<TaskDto>>>(JsonOpts);
        body!.Data!.Items.Select(t => t.Title).Should().Equal("Product work");
    }

    [Fact]
    public async Task ListTasks_with_excludeDone_hides_done_tasks_and_sorts_non_active_tasks_by_due_date()
    {
        // Due-date order still governs among non-Active tasks — Active tasks sort by recency
        // instead (see ListTasks_sorts_active_tasks_first_by_most_recently_activated below), so
        // these two are blocked to land them in the non-Active group this test actually covers.
        var engineer = await SeedEngineerAsync("task_exclude_done@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("ExcludeDone project");
        var dueSoon  = await SeedTaskAsync("Due soon",  project.Id, dueDaysFromNow: 2,  assigneeId: engineer.Id);
        var dueLater = await SeedTaskAsync("Due later", project.Id, dueDaysFromNow: 20, assigneeId: engineer.Id);
        var doneTask = await SeedTaskAsync("Already done", project.Id, dueDaysFromNow: 1, assigneeId: engineer.Id);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var done = await db.Tasks.FirstAsync(t => t.Id == doneTask.Id);
            done.MarkDone(engineer.Id);
            var soon = await db.Tasks.FirstAsync(t => t.Id == dueSoon.Id);
            soon.FlagBlocker("blocked for this test", engineer.Id);
            var later = await db.Tasks.FirstAsync(t => t.Id == dueLater.Id);
            later.FlagBlocker("blocked for this test", engineer.Id);
            await db.SaveChangesAsync();
        }

        var client = await AuthenticatedClientAsync("task_exclude_done@pulse.io");
        var response = await client.GetAsync($"/api/v1/tasks?assigneeId={engineer.Id}&excludeDone=true");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<PagedResult<TaskDto>>>(JsonOpts);
        var titles = body!.Data!.Items.Select(t => t.Title).ToList();
        titles.Should().Equal("Due soon", "Due later");
    }

    [Fact]
    public async Task ListTasks_sorts_active_tasks_first_by_most_recently_activated()
    {
        var engineer = await SeedEngineerAsync("task_sort_active@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("Active sort project");

        // Due date is deliberately the OPPOSITE of activation order — if the list were still
        // sorted by due date, "Due soon" would lead; under the new rule, activation recency wins.
        await SeedTaskAsync("First activated",  project.Id, dueDaysFromNow: 30, assigneeId: engineer.Id);
        await SeedTaskAsync("Second activated", project.Id, dueDaysFromNow: 1,  assigneeId: engineer.Id);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var blocked = PulseTask.Create("Blocked task", 3, project.Id,
                dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-5))); // earliest due date of all
            blocked.Assign(engineer.Id, engineer.Id);
            blocked.FlagBlocker("blocked", engineer.Id);
            db.Tasks.Add(blocked);
            await db.SaveChangesAsync();
        }

        var client = await AuthenticatedClientAsync("task_sort_active@pulse.io");
        var response = await client.GetAsync($"/api/v1/tasks?assigneeId={engineer.Id}&limit=100");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<PagedResult<TaskDto>>>(JsonOpts);
        var titles = body!.Data!.Items.Select(t => t.Title).ToList();

        // Both Active tasks (most-recently-activated first) precede the Blocked task, even though
        // the Blocked task has the earliest due date of the three.
        titles.Should().Equal("Second activated", "First activated", "Blocked task");
    }

    [Fact]
    public async Task ListTasks_pagination_does_not_skip_or_duplicate_rows_across_the_active_and_due_date_groups()
    {
        var engineer = await SeedEngineerAsync("task_sort_paging@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("Active sort paging project");

        await SeedTaskAsync("Active A", project.Id, dueDaysFromNow: 30, assigneeId: engineer.Id);
        await SeedTaskAsync("Active B", project.Id, dueDaysFromNow: 1,  assigneeId: engineer.Id);
        await SeedTaskAsync("Active C", project.Id, dueDaysFromNow: 15, assigneeId: engineer.Id);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            var blockedDated = PulseTask.Create("Blocked dated", 3, project.Id,
                dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)));
            blockedDated.AssignTaskNumber(4); // 1-3 already taken by the SeedTaskAsync calls above
            blockedDated.Assign(engineer.Id, engineer.Id);
            blockedDated.FlagBlocker("blocked", engineer.Id);
            db.Tasks.Add(blockedDated);

            var blockedUndated = PulseTask.Create("Blocked undated", 0, project.Id);
            blockedUndated.AssignTaskNumber(5);
            blockedUndated.Assign(engineer.Id, engineer.Id);
            db.Tasks.Add(blockedUndated);

            await db.SaveChangesAsync();
        }

        var client = await AuthenticatedClientAsync("task_sort_paging@pulse.io");

        var fullResponse = await client.GetAsync($"/api/v1/tasks?assigneeId={engineer.Id}&limit=100");
        var fullBody = await fullResponse.Content.ReadFromJsonAsync<ApiResponse<PagedResult<TaskDto>>>(JsonOpts);
        var expectedTitles = fullBody!.Data!.Items.Select(t => t.Title).ToList();
        expectedTitles.Should().HaveCount(5);

        // Re-fetch one row at a time via the cursor and confirm it reconstructs the exact same
        // sequence — proving the composite (active-group / due-date-group) cursor doesn't skip or
        // repeat a row at any group boundary.
        var pagedTitles = new List<string>();
        string? cursor = null;
        for (var i = 0; i < expectedTitles.Count; i++)
        {
            var url = $"/api/v1/tasks?assigneeId={engineer.Id}&limit=1" + (cursor is not null ? $"&cursor={Uri.EscapeDataString(cursor)}" : "");
            var pageResponse = await client.GetAsync(url);
            pageResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var pageBody = await pageResponse.Content.ReadFromJsonAsync<ApiResponse<PagedResult<TaskDto>>>(JsonOpts);
            pageBody!.Data!.Items.Should().ContainSingle();
            pagedTitles.Add(pageBody.Data.Items[0].Title);
            cursor = pageBody.Data.NextCursor;
        }

        pagedTitles.Should().Equal(expectedTitles);
        cursor.Should().BeNull("the last page should report no further cursor");
    }

    [Fact]
    public async Task ListTasks_rejects_a_stale_pre_two_group_cursor_instead_of_500ing()
    {
        // A cursor bookmarked from before this sort change used the old "{dueDate|null}|{id}"
        // shape — it must surface as an ordinary validation error, not corrupt results or crash.
        var engineer = await SeedEngineerAsync("task_sort_stale_cursor@pulse.io", Roles.Engineer);
        var client = await AuthenticatedClientAsync("task_sort_stale_cursor@pulse.io");
        var staleCursor = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"2026-06-23|{Guid.NewGuid()}"));

        var response = await client.GetAsync($"/api/v1/tasks?assigneeId={engineer.Id}&cursor={Uri.EscapeDataString(staleCursor)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<object>>(JsonOpts);
        body!.Error!.Code.Should().Be("VALIDATION_ERROR");
    }

    [Fact]
    public async Task Engineer_sees_a_peers_task_but_not_their_team_leads_task_on_a_shared_project()
    {
        var lead = await SeedEngineerAsync("visibility_lead@pulse.io", Roles.TeamLead);
        var team = await SeedTeamAsync("Visibility team", lead.Id);
        var engineer = await SeedEngineerAsync("visibility_eng@pulse.io", Roles.Engineer);
        var peer = await SeedEngineerAsync("visibility_peer@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(engineer.Id, team.Id);
        await AssignEngineerToTeamAsync(peer.Id, team.Id);
        await AssignEngineerToTeamAsync(lead.Id, team.Id);
        var project = await SeedProjectAsync("Visibility project", team.Id);
        await SeedProjectMemberAsync(project.Id, engineer.Id);
        await SeedTaskAsync("Peer's task", project.Id, assigneeId: peer.Id);
        await SeedTaskAsync("Lead's own task", project.Id, assigneeId: lead.Id);
        var client = await AuthenticatedClientAsync("visibility_eng@pulse.io");

        var response = await client.GetAsync($"/api/v1/tasks?projectId={project.Id}&limit=100");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<PagedResult<TaskDto>>>(JsonOpts);
        var titles = body!.Data!.Items.Select(t => t.Title).ToList();
        titles.Should().Contain("Peer's task");
        titles.Should().NotContain("Lead's own task");
    }

    [Fact]
    public async Task TeamLead_only_sees_their_own_teams_tasks_not_the_whole_company()
    {
        var lead        = await SeedEngineerAsync("scoped_lead@pulse.io", Roles.TeamLead);
        var leadTeam    = await SeedTeamAsync("Lead's own team", lead.Id);
        var otherTeam   = await SeedTeamAsync("Some other team");
        var ownReport   = await SeedEngineerAsync("scoped_lead_report@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(lead.Id, leadTeam.Id);
        await AssignEngineerToTeamAsync(ownReport.Id, leadTeam.Id);
        var ownProject   = await SeedProjectAsync("Lead's own project", leadTeam.Id);
        var otherProject = await SeedProjectAsync("Unrelated project", otherTeam.Id);
        await SeedTaskAsync("My team's task", ownProject.Id, assigneeId: ownReport.Id);
        await SeedTaskAsync("Someone else's task", otherProject.Id);
        var client = await AuthenticatedClientAsync("scoped_lead@pulse.io");

        var response = await client.GetAsync("/api/v1/tasks?limit=100");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<PagedResult<TaskDto>>>(JsonOpts);
        var titles = body!.Data!.Items.Select(t => t.Title).ToList();
        titles.Should().Contain("My team's task");
        titles.Should().NotContain("Someone else's task");
    }

    [Fact]
    public async Task TeamLead_is_still_scoped_to_their_led_team_when_their_own_TeamId_is_unset()
    {
        // Creating a team with a designated lead never assigns that lead as one of its own
        // members — Team.TeamLeadId is the only link. A lead whose own Engineer.TeamId was
        // never separately set (e.g. right after CreateTeamCommand) must still be scoped to the
        // team they lead, not fall through to "unscoped, sees everything".
        var lead      = await SeedEngineerAsync("unsynced_lead@pulse.io", Roles.TeamLead);
        var leadTeam  = await SeedTeamAsync("Unsynced lead team", lead.Id);
        var otherTeam = await SeedTeamAsync("Unsynced other team");
        var ownReport = await SeedEngineerAsync("unsynced_lead_report@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(ownReport.Id, leadTeam.Id);
        // lead.Id is intentionally NOT assigned to leadTeam.Id here.
        var ownProject   = await SeedProjectAsync("Unsynced lead project", leadTeam.Id);
        var otherProject = await SeedProjectAsync("Unsynced unrelated project", otherTeam.Id);
        await SeedTaskAsync("My team's task", ownProject.Id, assigneeId: ownReport.Id);
        await SeedTaskAsync("Someone else's task", otherProject.Id);
        var client = await AuthenticatedClientAsync("unsynced_lead@pulse.io");

        var response = await client.GetAsync("/api/v1/tasks?limit=100");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<PagedResult<TaskDto>>>(JsonOpts);
        var titles = body!.Data!.Items.Select(t => t.Title).ToList();
        titles.Should().Contain("My team's task");
        titles.Should().NotContain("Someone else's task");
    }

    [Fact]
    public async Task TeamLead_cannot_open_a_task_on_an_unrelated_team_by_direct_link()
    {
        var lead        = await SeedEngineerAsync("directlink_lead@pulse.io", Roles.TeamLead);
        var leadTeam    = await SeedTeamAsync("Direct link lead team", lead.Id);
        var otherTeam   = await SeedTeamAsync("Direct link other team");
        await AssignEngineerToTeamAsync(lead.Id, leadTeam.Id);
        var otherProject = await SeedProjectAsync("Unrelated direct link project", otherTeam.Id);
        var otherTask    = await SeedTaskAsync("Someone else's task", otherProject.Id);
        var client = await AuthenticatedClientAsync("directlink_lead@pulse.io");

        var response = await client.GetAsync($"/api/v1/tasks/{otherTask.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task TeamLead_can_open_a_task_on_their_own_teams_project_by_direct_link()
    {
        var lead     = await SeedEngineerAsync("ownproject_lead@pulse.io", Roles.TeamLead);
        var leadTeam = await SeedTeamAsync("Own project lead team", lead.Id);
        await AssignEngineerToTeamAsync(lead.Id, leadTeam.Id);
        var project = await SeedProjectAsync("Own team project", leadTeam.Id);
        var task    = await SeedTaskAsync("My team's task", project.Id);
        var client = await AuthenticatedClientAsync("ownproject_lead@pulse.io");

        var response = await client.GetAsync($"/api/v1/tasks/{task.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── pause / resume ───────────────────────────────────────────────────────

    [Fact]
    public async Task PauseTask_removes_it_from_the_engineers_active_workload()
    {
        var engineer = await SeedEngineerAsync("pause_workload@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("Pause workload project");
        var task1 = await SeedTaskAsync("Task 1", project.Id, assigneeId: engineer.Id);
        await SeedTaskAsync("Task 2", project.Id, assigneeId: engineer.Id);
        var client = await AuthenticatedClientAsync("pause_workload@pulse.io");

        var before = await client.GetAsync("/api/v1/reports/me");
        var beforeBody = await before.Content.ReadFromJsonAsync<ApiResponse<OverworkSignalsDto>>(JsonOpts);
        beforeBody!.Data!.Concurrent.Reason.Should().StartWith("2 concurrent");

        var pauseResp = await client.PostAsJsonAsync($"/api/v1/tasks/{task1.Id}/pause", new { note = "Taking a break" });
        pauseResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await client.GetAsync("/api/v1/reports/me");
        var afterBody = await after.Content.ReadFromJsonAsync<ApiResponse<OverworkSignalsDto>>(JsonOpts);
        afterBody!.Data!.Concurrent.Reason.Should().StartWith("1 concurrent");
    }

    [Fact]
    public async Task ResumeTask_restores_it_to_the_engineers_active_workload()
    {
        var engineer = await SeedEngineerAsync("resume_workload@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("Resume workload project");
        var task = await SeedTaskAsync("Task 1", project.Id, assigneeId: engineer.Id);
        var client = await AuthenticatedClientAsync("resume_workload@pulse.io");

        await client.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/pause", new { note = (string?)null });

        var resumeResp = await client.DeleteAsync($"/api/v1/tasks/{task.Id}/pause");
        resumeResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var resumed = await resumeResp.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        resumed!.Data!.Status.Should().Be("active");
        resumed.Data!.PauseNote.Should().BeNull();

        var after = await client.GetAsync("/api/v1/reports/me");
        var afterBody = await after.Content.ReadFromJsonAsync<ApiResponse<OverworkSignalsDto>>(JsonOpts);
        afterBody!.Data!.Concurrent.Reason.Should().StartWith("1 concurrent");
    }

    [Fact]
    public async Task SendingATaskToQa_also_removes_it_from_the_engineers_active_workload()
    {
        var engineer = await SeedEngineerAsync("qa_workload@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("QA workload project");
        var task1 = await SeedTaskAsync("Task 1", project.Id, assigneeId: engineer.Id, requiresQa: true);
        await SeedTaskAsync("Task 2", project.Id, assigneeId: engineer.Id);
        var client = await AuthenticatedClientAsync("qa_workload@pulse.io");

        var sendToQaResp = await client.PostAsync($"/api/v1/tasks/{task1.Id}/send-to-qa", null);
        sendToQaResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await client.GetAsync("/api/v1/reports/me");
        var afterBody = await after.Content.ReadFromJsonAsync<ApiResponse<OverworkSignalsDto>>(JsonOpts);
        afterBody!.Data!.Concurrent.Reason.Should().StartWith("1 concurrent");
    }

    [Fact]
    public async Task PauseTask_returns_403_when_caller_is_not_the_assignee()
    {
        var assignee = await SeedEngineerAsync("pause_denied_assignee@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("pause_denied_other@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("Pause denied project");
        var task = await SeedTaskAsync("Task", project.Id, assigneeId: assignee.Id);
        var client = await AuthenticatedClientAsync("pause_denied_other@pulse.io");

        var response = await client.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/pause", new { note = (string?)null });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task HeadOfPmo_can_pause_a_task_they_are_not_assigned_to()
    {
        var assignee = await SeedEngineerAsync("pause_head_assignee@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("pause_head@pulse.io", Roles.HeadOfPmo);
        var project = await SeedProjectAsync("Pause head project");
        var task = await SeedTaskAsync("Task", project.Id, assigneeId: assignee.Id);
        var client = await AuthenticatedClientAsync("pause_head@pulse.io");

        var response = await client.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/pause", new { note = "Reprioritized by PMO" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        body!.Data!.Status.Should().Be("paused");
    }

    // ── task key / external reference ────────────────────────────────────────

    [Fact]
    public async Task Tasks_created_in_the_same_project_get_sequential_numbers()
    {
        await SeedEngineerAsync("taskkey_pm@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("Task Key Project", code: "TKEY");
        var client = await AuthenticatedClientAsync("taskkey_pm@pulse.io");

        var first = await (await client.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "First task", projectId = project.Id,
        })).Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        var second = await (await client.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Second task", projectId = project.Id,
        })).Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);

        first!.Data!.TaskNumber.Should().Be(1);
        second!.Data!.TaskNumber.Should().Be(2);

        var fetched = await (await client.GetAsync($"/api/v1/tasks/{first.Data.Id}"))
            .Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        fetched!.Data!.TaskKey.Should().Be("TKEY-1");
    }

    [Fact]
    public async Task ExternalReference_round_trips_through_create_and_update()
    {
        await SeedEngineerAsync("extref_pm@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("External Ref Project");
        var client = await AuthenticatedClientAsync("extref_pm@pulse.io");

        var created = await (await client.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Imported task", projectId = project.Id, externalReference = "JIRA-482",
        })).Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        created!.Data!.ExternalReference.Should().Be("JIRA-482");

        var updated = await (await client.PatchAsJsonAsync($"/api/v1/tasks/{created.Data.Id}",
            new { externalReference = "JIRA-999" })).Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        updated!.Data!.ExternalReference.Should().Be("JIRA-999");

        var cleared = await (await client.PatchAsJsonAsync($"/api/v1/tasks/{created.Data.Id}",
            new { removeExternalReference = true })).Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        cleared!.Data!.ExternalReference.Should().BeNull();
    }
}
