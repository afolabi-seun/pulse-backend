using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Notifications;
using Pulse.Application.Overwork;
using Pulse.Application.Tasks;
using Pulse.Application.Tasks.Commands;
using Pulse.Domain.Engineers;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Tasks;

[Collection("Integration")]
public class TasksAdvancedTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public TasksAdvancedTests(PulseWebApplicationFactory factory) : base(factory) { }

    // ── preview-assign ────────────────────────────────────────────────────────

    [Fact]
    public async Task PreviewAssign_returns_before_and_after_signals()
    {
        var assignee = await SeedEngineerAsync("pa_eng@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("pa_pm@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("PreviewAssign project");
        var task = await SeedTaskAsync("Task to preview", project.Id);
        var pmClient = await AuthenticatedClientAsync("pa_pm@pulse.io");

        var response = await pmClient.PostAsJsonAsync(
            $"/api/v1/tasks/{task.Id}/preview-assign",
            new { targetEngineerId = assignee.Id });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<PreviewAssignResult>>(JsonOpts);
        body!.Data.Should().NotBeNull();
        body.Data!.Before.Should().NotBeNull();
        body.Data.After.Should().NotBeNull();
    }

    [Fact]
    public async Task PreviewAssign_returns_400_when_engineer_id_missing()
    {
        await SeedEngineerAsync("pa_pm2@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("PreviewAssign validation project");
        var task = await SeedTaskAsync("Validation task", project.Id);
        var pmClient = await AuthenticatedClientAsync("pa_pm2@pulse.io");

        var response = await pmClient.PostAsJsonAsync(
            $"/api/v1/tasks/{task.Id}/preview-assign",
            new { targetEngineerId = Guid.Empty });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PreviewAssign_returns_403_for_engineer_role()
    {
        await SeedEngineerAsync("pa_eng2@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("pa_pm3@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("PA forbidden project");
        var task = await SeedTaskAsync("Forbidden preview", project.Id);
        var engClient = await AuthenticatedClientAsync("pa_eng2@pulse.io");

        var response = await engClient.PostAsJsonAsync(
            $"/api/v1/tasks/{task.Id}/preview-assign",
            new { targetEngineerId = Guid.NewGuid() });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── bulk-reassign ─────────────────────────────────────────────────────────

    [Fact]
    public async Task BulkReassign_reassigns_tasks_and_returns_signals()
    {
        var source = await SeedEngineerAsync("br_src@pulse.io", Roles.Engineer);
        var target = await SeedEngineerAsync("br_tgt@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("br_pm@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("BulkReassign project");
        var task1 = await SeedTaskAsync("Task 1", project.Id, assigneeId: source.Id);
        var task2 = await SeedTaskAsync("Task 2", project.Id, assigneeId: source.Id);
        var pmClient = await AuthenticatedClientAsync("br_pm@pulse.io");

        var response = await pmClient.PostAsJsonAsync("/api/v1/tasks/bulk-reassign", new
        {
            taskIds = new[] { task1.Id, task2.Id },
            targetEngineerId = target.Id
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<BulkReassignResult>>(JsonOpts);
        body!.Data!.TasksReassigned.Should().Be(2);
        body.Data.SignalsAfter.Should().NotBeNull();
    }

    [Fact]
    public async Task BulkReassign_returns_400_when_task_ids_empty()
    {
        await SeedEngineerAsync("br_val@pulse.io", Roles.ProjectManager);
        var pmClient = await AuthenticatedClientAsync("br_val@pulse.io");

        var response = await pmClient.PostAsJsonAsync("/api/v1/tasks/bulk-reassign", new
        {
            taskIds = Array.Empty<Guid>(),
            targetEngineerId = Guid.NewGuid()
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task BulkReassign_returns_403_for_engineer_role()
    {
        await SeedEngineerAsync("br_eng@pulse.io", Roles.Engineer);
        var engClient = await AuthenticatedClientAsync("br_eng@pulse.io");

        var response = await engClient.PostAsJsonAsync("/api/v1/tasks/bulk-reassign", new
        {
            taskIds = new[] { Guid.NewGuid() },
            targetEngineerId = Guid.NewGuid()
        });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── bulk-create ────────────────────────────────────────────────────────────

    [Fact]
    public async Task BulkCreate_returns_201_with_created_count()
    {
        await SeedEngineerAsync("bc_pm@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("BulkCreate project");
        var pmClient = await AuthenticatedClientAsync("bc_pm@pulse.io");
        var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)).ToString("yyyy-MM-dd");

        var response = await pmClient.PostAsJsonAsync("/api/v1/tasks/bulk", new
        {
            projectId = project.Id,
            tasks = new[]
            {
                new { title = "Task A", points = 2, dueDate = tomorrow },
                new { title = "Task B", points = 3, dueDate = tomorrow }
            }
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<BulkCreateTasksResult>>(JsonOpts);
        body!.Data!.Created.Should().Be(2);
        body.Data.Failed.Should().BeEmpty();
    }

    [Fact]
    public async Task BulkCreate_allows_omitting_points_on_an_item()
    {
        await SeedEngineerAsync("bc_no_points_pm@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("BulkCreate no points project");
        var pmClient = await AuthenticatedClientAsync("bc_no_points_pm@pulse.io");
        var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)).ToString("yyyy-MM-dd");

        var response = await pmClient.PostAsJsonAsync("/api/v1/tasks/bulk", new
        {
            projectId = project.Id,
            tasks = new[]
            {
                new { title = "Ungroomed bulk task", dueDate = tomorrow },
            }
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<BulkCreateTasksResult>>(JsonOpts);
        body!.Data!.Created.Should().Be(1);
        body.Data.Failed.Should().BeEmpty();
    }

    [Fact]
    public async Task BulkCreate_returns_partial_result_when_some_items_invalid()
    {
        await SeedEngineerAsync("bc_pm2@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("BulkCreate partial project");
        var pmClient = await AuthenticatedClientAsync("bc_pm2@pulse.io");
        var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)).ToString("yyyy-MM-dd");

        var response = await pmClient.PostAsJsonAsync("/api/v1/tasks/bulk", new
        {
            projectId = project.Id,
            tasks = new object[]
            {
                new { title = "Valid task", points = 2, dueDate = tomorrow },
                new { title = "Unknown assignee", points = 2, dueDate = tomorrow, assigneeId = Guid.NewGuid() },
            }
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<BulkCreateTasksResult>>(JsonOpts);
        body!.Data!.Created.Should().Be(1);
        body.Data.Failed.Should().HaveCount(1);
        body.Data.Failed[0].Index.Should().Be(1);
    }

    [Fact]
    public async Task BulkCreate_sets_acceptance_criteria_when_provided()
    {
        await SeedEngineerAsync("bc_ac_pm@pulse.io", Roles.ProjectManager);
        var project = await SeedProjectAsync("BulkCreate AC project");
        var pmClient = await AuthenticatedClientAsync("bc_ac_pm@pulse.io");
        var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)).ToString("yyyy-MM-dd");

        var response = await pmClient.PostAsJsonAsync("/api/v1/tasks/bulk", new
        {
            projectId = project.Id,
            tasks = new[]
            {
                new { title = "Task with AC", points = 2, dueDate = tomorrow, acceptanceCriteria = "Given X, when Y, then Z." },
            }
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<BulkCreateTasksResult>>(JsonOpts);
        body!.Data!.Created.Should().Be(1);

        var listResponse = await pmClient.GetAsync($"/api/v1/tasks?projectId={project.Id}");
        var listBody = await listResponse.Content.ReadFromJsonAsync<ApiResponse<PagedResult<TaskDto>>>(JsonOpts);
        listBody!.Data!.Items.Should().ContainSingle()
            .Which.AcceptanceCriteria.Should().Be("Given X, when Y, then Z.");
    }

    [Fact]
    public async Task BulkCreate_returns_400_when_project_not_found()
    {
        await SeedEngineerAsync("bc_pm3@pulse.io", Roles.ProjectManager);
        var pmClient = await AuthenticatedClientAsync("bc_pm3@pulse.io");
        var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)).ToString("yyyy-MM-dd");

        var response = await pmClient.PostAsJsonAsync("/api/v1/tasks/bulk", new
        {
            projectId = Guid.NewGuid(),
            tasks = new[] { new { title = "Task", points = 2, dueDate = tomorrow } }
        });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── loan task ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task LoanTask_reassigns_task_to_engineer_on_different_team()
    {
        var lead = await SeedEngineerAsync("loan_lead@pulse.io", Roles.TeamLead);
        var member = await SeedEngineerAsync("loan_member@pulse.io", Roles.Engineer);
        var borrower = await SeedEngineerAsync("loan_borrower@pulse.io", Roles.Engineer);
        var teamA = await SeedTeamAsync("Team A", lead.Id);
        var teamB = await SeedTeamAsync("Team B");

        // Assign engineers to their teams
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Pulse.Infrastructure.Persistence.PulseDbContext>();
            var eng = await db.Engineers.FindAsync(member.Id);
            eng!.AssignToTeam(teamA.Id);
            var bor = await db.Engineers.FindAsync(borrower.Id);
            bor!.AssignToTeam(teamB.Id);
            await db.SaveChangesAsync();
        }

        var project = await SeedProjectAsync("Loan project");
        var task = await SeedTaskAsync("Task to loan", project.Id, assigneeId: member.Id);
        var leadClient = await AuthenticatedClientAsync("loan_lead@pulse.io");

        var response = await leadClient.PostAsJsonAsync(
            $"/api/v1/tasks/{task.Id}/loan",
            new { targetEngineerId = borrower.Id, reason = "Needed for sprint" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        body!.Data!.AssigneeId.Should().Be(borrower.Id);
    }

    [Fact]
    public async Task LoanTask_returns_422_when_target_on_same_team()
    {
        var lead = await SeedEngineerAsync("loan_same_lead@pulse.io", Roles.TeamLead);
        var member = await SeedEngineerAsync("loan_same_member@pulse.io", Roles.Engineer);
        var target = await SeedEngineerAsync("loan_same_target@pulse.io", Roles.Engineer);
        var teamA = await SeedTeamAsync("Loan Same Team", lead.Id);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Pulse.Infrastructure.Persistence.PulseDbContext>();
            (await db.Engineers.FindAsync(member.Id))!.AssignToTeam(teamA.Id);
            (await db.Engineers.FindAsync(target.Id))!.AssignToTeam(teamA.Id);
            await db.SaveChangesAsync();
        }

        var project = await SeedProjectAsync("Loan same team project");
        var task = await SeedTaskAsync("Task on same team", project.Id, assigneeId: member.Id);
        var leadClient = await AuthenticatedClientAsync("loan_same_lead@pulse.io");

        var response = await leadClient.PostAsJsonAsync(
            $"/api/v1/tasks/{task.Id}/loan",
            new { targetEngineerId = target.Id });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task LoanTask_returns_422_when_task_unassigned()
    {
        var lead = await SeedEngineerAsync("loan_unassigned_lead@pulse.io", Roles.TeamLead);
        var target = await SeedEngineerAsync("loan_unassigned_tgt@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("Loan unassigned project");
        var task = await SeedTaskAsync("Unassigned task", project.Id); // no assigneeId
        var leadClient = await AuthenticatedClientAsync("loan_unassigned_lead@pulse.io");

        var response = await leadClient.PostAsJsonAsync(
            $"/api/v1/tasks/{task.Id}/loan",
            new { targetEngineerId = target.Id });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task LoanTask_returns_422_when_target_is_in_a_different_team_but_same_department()
    {
        var lead = await SeedEngineerAsync("loan_dept_lead@pulse.io", Roles.TeamLead);
        var member = await SeedEngineerAsync("loan_dept_member@pulse.io", Roles.Engineer);
        var target = await SeedEngineerAsync("loan_dept_target@pulse.io", Roles.Engineer);
        var teamA = await SeedTeamAsync("Dept Team A", lead.Id, department: "Engineering");
        var teamB = await SeedTeamAsync("Dept Team B", department: "Engineering");

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Pulse.Infrastructure.Persistence.PulseDbContext>();
            (await db.Engineers.FindAsync(member.Id))!.AssignToTeam(teamA.Id);
            (await db.Engineers.FindAsync(target.Id))!.AssignToTeam(teamB.Id);
            await db.SaveChangesAsync();
        }

        var project = await SeedProjectAsync("Loan same department project");
        var task = await SeedTaskAsync("Task in same department", project.Id, assigneeId: member.Id);
        var leadClient = await AuthenticatedClientAsync("loan_dept_lead@pulse.io");

        var response = await leadClient.PostAsJsonAsync(
            $"/api/v1/tasks/{task.Id}/loan",
            new { targetEngineerId = target.Id });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task LoanTask_reassigns_task_to_engineer_in_a_different_department()
    {
        var lead = await SeedEngineerAsync("loan_xdept_lead@pulse.io", Roles.TeamLead);
        var member = await SeedEngineerAsync("loan_xdept_member@pulse.io", Roles.Engineer);
        var borrower = await SeedEngineerAsync("loan_xdept_borrower@pulse.io", Roles.Engineer);
        var teamA = await SeedTeamAsync("Xdept Team A", lead.Id, department: "Engineering");
        var teamB = await SeedTeamAsync("Xdept Team B", department: "Design");

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Pulse.Infrastructure.Persistence.PulseDbContext>();
            (await db.Engineers.FindAsync(member.Id))!.AssignToTeam(teamA.Id);
            (await db.Engineers.FindAsync(borrower.Id))!.AssignToTeam(teamB.Id);
            await db.SaveChangesAsync();
        }

        var project = await SeedProjectAsync("Loan cross department project");
        var task = await SeedTaskAsync("Task to loan cross department", project.Id, assigneeId: member.Id);
        var leadClient = await AuthenticatedClientAsync("loan_xdept_lead@pulse.io");

        var response = await leadClient.PostAsJsonAsync(
            $"/api/v1/tasks/{task.Id}/loan",
            new { targetEngineerId = borrower.Id, reason = "Cross-department expertise" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        body!.Data!.AssigneeId.Should().Be(borrower.Id);
    }

    [Fact]
    public async Task LoanTask_notifies_the_borrower()
    {
        var lead = await SeedEngineerAsync("loan_notify_lead@pulse.io", Roles.TeamLead);
        var member = await SeedEngineerAsync("loan_notify_member@pulse.io", Roles.Engineer);
        var borrower = await SeedEngineerAsync("loan_notify_borrower@pulse.io", Roles.Engineer);
        var teamA = await SeedTeamAsync("Notify Team A", lead.Id);
        var teamB = await SeedTeamAsync("Notify Team B");
        await AssignEngineerToTeamAsync(member.Id, teamA.Id);
        await AssignEngineerToTeamAsync(borrower.Id, teamB.Id);

        var project = await SeedProjectAsync("Loan notify project");
        var task = await SeedTaskAsync("Task to loan and notify", project.Id, assigneeId: member.Id);
        var leadClient = await AuthenticatedClientAsync("loan_notify_lead@pulse.io");

        var response = await leadClient.PostAsJsonAsync(
            $"/api/v1/tasks/{task.Id}/loan",
            new { targetEngineerId = borrower.Id, reason = "Needed for sprint" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var borrowerClient = await AuthenticatedClientAsync("loan_notify_borrower@pulse.io");
        var notifResponse = await borrowerClient.GetAsync("/api/v1/notifications");
        var notifications = (await notifResponse.Content.ReadFromJsonAsync<ApiResponse<PagedResult<NotificationDto>>>(JsonOpts))!.Data!;

        notifications.Items.Should().Contain(n => n.Kind == "task_loaned");
    }

    [Fact]
    public async Task LoanTask_allows_a_department_head_to_loan_a_task_not_on_any_team_they_lead()
    {
        // Head of PMO leads no team at all — the task's current assignee is on someone else's
        // team entirely, which used to fail with "you do not lead any team".
        var head = await SeedEngineerAsync("loan_head@pulse.io", Roles.HeadOfPmo);
        var member = await SeedEngineerAsync("loan_head_member@pulse.io", Roles.Engineer);
        var borrower = await SeedEngineerAsync("loan_head_borrower@pulse.io", Roles.Engineer);
        var teamA = await SeedTeamAsync("Head Loan Team A", department: "Engineering");
        var teamB = await SeedTeamAsync("Head Loan Team B", department: "Design");

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Pulse.Infrastructure.Persistence.PulseDbContext>();
            (await db.Engineers.FindAsync(member.Id))!.AssignToTeam(teamA.Id);
            (await db.Engineers.FindAsync(borrower.Id))!.AssignToTeam(teamB.Id);
            await db.SaveChangesAsync();
        }

        var project = await SeedProjectAsync("Loan head project");
        var task = await SeedTaskAsync("Task not on head's team", project.Id, assigneeId: member.Id);
        var headClient = await AuthenticatedClientAsync("loan_head@pulse.io");

        var response = await headClient.PostAsJsonAsync(
            $"/api/v1/tasks/{task.Id}/loan",
            new { targetEngineerId = borrower.Id, reason = "PMO coordinating capacity" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        body!.Data!.AssigneeId.Should().Be(borrower.Id);
    }

    [Fact]
    public async Task LoanTask_still_returns_422_for_a_head_when_target_shares_the_assignees_department()
    {
        // The department comparison is against the CURRENT ASSIGNEE's department (Head of PMO
        // has no department of their own to compare against), so this must still be rejected.
        var head = await SeedEngineerAsync("loan_head_dept@pulse.io", Roles.HeadOfPmo);
        var member = await SeedEngineerAsync("loan_head_dept_member@pulse.io", Roles.Engineer);
        var target = await SeedEngineerAsync("loan_head_dept_target@pulse.io", Roles.Engineer);
        var teamA = await SeedTeamAsync("Head Dept Team A", department: "Engineering");
        var teamB = await SeedTeamAsync("Head Dept Team B", department: "Engineering");

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Pulse.Infrastructure.Persistence.PulseDbContext>();
            (await db.Engineers.FindAsync(member.Id))!.AssignToTeam(teamA.Id);
            (await db.Engineers.FindAsync(target.Id))!.AssignToTeam(teamB.Id);
            await db.SaveChangesAsync();
        }

        var project = await SeedProjectAsync("Loan head same department project");
        var task = await SeedTaskAsync("Task same department as head target", project.Id, assigneeId: member.Id);
        var headClient = await AuthenticatedClientAsync("loan_head_dept@pulse.io");

        var response = await headClient.PostAsJsonAsync(
            $"/api/v1/tasks/{task.Id}/loan",
            new { targetEngineerId = target.Id });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task LoanTask_lets_a_pm_loan_any_task()
    {
        // PMO (project_manager) isn't the designated lead of any single team, same as a
        // department head — gained the same company-wide loan/recall reach here. Target is left
        // teamless (no department) so it's guaranteed to differ from the assignee's department.
        await SeedEngineerAsync("loan_pm@pulse.io", Roles.ProjectManager);
        var team = await SeedTeamAsync("Loan PM team", department: "Engineering");
        var assignee = await SeedEngineerAsync("loan_pm_assignee@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(assignee.Id, team.Id);
        var target = await SeedEngineerAsync("loan_pm_target@pulse.io", Roles.Designer);
        var project = await SeedProjectAsync("Loan PM project", team.Id);
        var task = await SeedTaskAsync("PM loan task", project.Id, assigneeId: assignee.Id);
        var pmClient = await AuthenticatedClientAsync("loan_pm@pulse.io");

        var response = await pmClient.PostAsJsonAsync(
            $"/api/v1/tasks/{task.Id}/loan",
            new { targetEngineerId = target.Id });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RecallTask_lets_a_pm_recall_any_task()
    {
        await SeedEngineerAsync("recall_pm@pulse.io", Roles.ProjectManager);
        var team = await SeedTeamAsync("Recall PM team", department: "Engineering");
        var original = await SeedEngineerAsync("recall_pm_original@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(original.Id, team.Id);
        var borrower = await SeedEngineerAsync("recall_pm_borrower@pulse.io", Roles.Designer);
        var project = await SeedProjectAsync("Recall PM project", team.Id);
        var task = await SeedTaskAsync("PM recall task", project.Id, assigneeId: original.Id);
        var pmClient = await AuthenticatedClientAsync("recall_pm@pulse.io");

        var loanResponse = await pmClient.PostAsJsonAsync(
            $"/api/v1/tasks/{task.Id}/loan",
            new { targetEngineerId = borrower.Id });
        loanResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var recallResponse = await pmClient.PostAsync($"/api/v1/tasks/{task.Id}/recall", null);

        recallResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── assign task ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AssignTask_lets_a_team_lead_claim_unassigned_work_for_their_own_team_member()
    {
        var lead = await SeedEngineerAsync("assign_lead@pulse.io", Roles.TeamLead);
        var member = await SeedEngineerAsync("assign_lead_member@pulse.io", Roles.Engineer);
        var team = await SeedTeamAsync("Assign Lead Team", lead.Id);
        await AssignEngineerToTeamAsync(member.Id, team.Id);

        var project = await SeedProjectAsync("Assign lead project", team.Id);
        var task = await SeedTaskAsync("Unclaimed backlog task", project.Id);
        var leadClient = await AuthenticatedClientAsync("assign_lead@pulse.io");

        var response = await leadClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/assign", new { assigneeId = member.Id });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        body!.Data!.AssigneeId.Should().Be(member.Id);
    }

    [Fact]
    public async Task AssignTask_returns_422_when_team_lead_targets_an_engineer_outside_their_team()
    {
        var lead = await SeedEngineerAsync("assign_lead_outside@pulse.io", Roles.TeamLead);
        var outsider = await SeedEngineerAsync("assign_outsider@pulse.io", Roles.Engineer);
        var leadTeam = await SeedTeamAsync("Assign Outside Lead Team", lead.Id);
        var otherTeam = await SeedTeamAsync("Assign Outside Other Team");
        await AssignEngineerToTeamAsync(outsider.Id, otherTeam.Id);

        var project = await SeedProjectAsync("Assign outside project", leadTeam.Id);
        var task = await SeedTaskAsync("Unclaimed task outsider target", project.Id);
        var leadClient = await AuthenticatedClientAsync("assign_lead_outside@pulse.io");

        var response = await leadClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/assign", new { assigneeId = outsider.Id });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        body!.Status.Should().Be("error");
    }

    [Fact]
    public async Task AssignTask_returns_403_for_a_plain_engineer()
    {
        var engineer = await SeedEngineerAsync("assign_denied_eng@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("Assign denied project");
        var task = await SeedTaskAsync("Unclaimed task denied", project.Id);
        var client = await AuthenticatedClientAsync("assign_denied_eng@pulse.io");

        var response = await client.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/assign", new { assigneeId = engineer.Id });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AssignTask_lets_a_project_manager_assign_across_teams_without_restriction()
    {
        await SeedEngineerAsync("assign_pm@pulse.io", Roles.ProjectManager);
        var target = await SeedEngineerAsync("assign_pm_target@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("Assign PM project");
        var task = await SeedTaskAsync("Unclaimed task for PM", project.Id);
        var pmClient = await AuthenticatedClientAsync("assign_pm@pulse.io");

        var response = await pmClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/assign", new { assigneeId = target.Id });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        body!.Data!.AssigneeId.Should().Be(target.Id);
    }

    [Fact]
    public async Task AssignTask_returns_422_when_the_task_already_has_an_assignee()
    {
        var lead = await SeedEngineerAsync("assign_already_lead@pulse.io", Roles.TeamLead);
        var member = await SeedEngineerAsync("assign_already_member@pulse.io", Roles.Engineer);
        var otherMember = await SeedEngineerAsync("assign_already_other@pulse.io", Roles.Engineer);
        var team = await SeedTeamAsync("Assign Already Team", lead.Id);
        await AssignEngineerToTeamAsync(member.Id, team.Id);
        await AssignEngineerToTeamAsync(otherMember.Id, team.Id);

        var project = await SeedProjectAsync("Assign already project", team.Id);
        var task = await SeedTaskAsync("Already assigned task", project.Id, assigneeId: member.Id);
        var leadClient = await AuthenticatedClientAsync("assign_already_lead@pulse.io");

        var response = await leadClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/assign", new { assigneeId = otherMember.Id });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    // ── return to backlog ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ReturnToBacklog_lets_a_team_lead_pull_back_their_own_teams_task()
    {
        var lead = await SeedEngineerAsync("rtb_lead@pulse.io", Roles.TeamLead);
        var member = await SeedEngineerAsync("rtb_member@pulse.io", Roles.Engineer);
        var team = await SeedTeamAsync("Return To Backlog Team", lead.Id);
        await AssignEngineerToTeamAsync(member.Id, team.Id);

        var project = await SeedProjectAsync("Return to backlog project", team.Id);
        var task = await SeedTaskAsync("Stuck task", project.Id, assigneeId: member.Id);
        var leadClient = await AuthenticatedClientAsync("rtb_lead@pulse.io");

        var response = await leadClient.PostAsync($"/api/v1/tasks/{task.Id}/return-to-backlog", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        body!.Data!.AssigneeId.Should().BeNull();
        body.Data.Status.Should().Be("backlog");
    }

    [Fact]
    public async Task ReturnToBacklog_returns_422_when_a_team_lead_targets_an_engineer_outside_their_team()
    {
        var lead = await SeedEngineerAsync("rtb_lead_outside@pulse.io", Roles.TeamLead);
        var outsider = await SeedEngineerAsync("rtb_outsider@pulse.io", Roles.Engineer);
        var leadTeam = await SeedTeamAsync("Return To Backlog Outside Lead Team", lead.Id);
        var otherTeam = await SeedTeamAsync("Return To Backlog Other Team");
        await AssignEngineerToTeamAsync(outsider.Id, otherTeam.Id);

        var project = await SeedProjectAsync("Return to backlog outside project", leadTeam.Id);
        var task = await SeedTaskAsync("Outsider's task", project.Id, assigneeId: outsider.Id);
        var leadClient = await AuthenticatedClientAsync("rtb_lead_outside@pulse.io");

        var response = await leadClient.PostAsync($"/api/v1/tasks/{task.Id}/return-to-backlog", null);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task ReturnToBacklog_returns_403_for_a_plain_engineer()
    {
        var engineer = await SeedEngineerAsync("rtb_denied_eng@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("Return to backlog denied project");
        var task = await SeedTaskAsync("Denied task", project.Id, assigneeId: engineer.Id);
        var client = await AuthenticatedClientAsync("rtb_denied_eng@pulse.io");

        var response = await client.PostAsync($"/api/v1/tasks/{task.Id}/return-to-backlog", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ReturnToBacklog_returns_422_for_a_task_that_is_not_Active()
    {
        var lead = await SeedEngineerAsync("rtb_notactive_lead@pulse.io", Roles.TeamLead);
        var project = await SeedProjectAsync("Return to backlog not-active project");
        var task = await SeedTaskAsync("Never claimed task", project.Id); // stays Backlog — no assignee
        var leadClient = await AuthenticatedClientAsync("rtb_notactive_lead@pulse.io");

        var response = await leadClient.PostAsync($"/api/v1/tasks/{task.Id}/return-to-backlog", null);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    // ── recall task ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RecallTask_hands_the_task_back_to_the_original_assignee_and_notifies_the_borrower()
    {
        var lead = await SeedEngineerAsync("recall_lead@pulse.io", Roles.TeamLead);
        var member = await SeedEngineerAsync("recall_member@pulse.io", Roles.Engineer);
        var borrower = await SeedEngineerAsync("recall_borrower@pulse.io", Roles.Engineer);
        var teamA = await SeedTeamAsync("Recall Team A", lead.Id);
        var teamB = await SeedTeamAsync("Recall Team B");
        await AssignEngineerToTeamAsync(member.Id, teamA.Id);
        await AssignEngineerToTeamAsync(borrower.Id, teamB.Id);

        var project = await SeedProjectAsync("Recall project");
        var task = await SeedTaskAsync("Task to loan then recall", project.Id, assigneeId: member.Id);
        var leadClient = await AuthenticatedClientAsync("recall_lead@pulse.io");

        var loanResponse = await leadClient.PostAsJsonAsync(
            $"/api/v1/tasks/{task.Id}/loan",
            new { targetEngineerId = borrower.Id, reason = "Needed for sprint" });
        loanResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var recallResponse = await leadClient.PostAsync($"/api/v1/tasks/{task.Id}/recall", null);

        recallResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await recallResponse.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        body!.Data!.AssigneeId.Should().Be(member.Id);

        var borrowerClient = await AuthenticatedClientAsync("recall_borrower@pulse.io");
        var notifResponse = await borrowerClient.GetAsync("/api/v1/notifications");
        var notifications = (await notifResponse.Content.ReadFromJsonAsync<ApiResponse<PagedResult<NotificationDto>>>(JsonOpts))!.Data!;
        notifications.Items.Should().Contain(n => n.Kind == "task_recalled");
    }

    [Fact]
    public async Task RecallTask_lets_the_current_borrower_recall_it_themselves_with_no_lead_or_head_role()
    {
        // Regression coverage at the real HTTP boundary — not just the handler — because the
        // endpoint used to carry its own role-only [RequiresCapability(TeamLeadOrHeadOnly,
        // PmoOnly)] gate, which would 403 a plain Engineer before the handler's own (correct)
        // self-recall check ever ran. The handler can't be the only place this is exercised.
        var lead = await SeedEngineerAsync("recall_self_lead@pulse.io", Roles.TeamLead);
        var member = await SeedEngineerAsync("recall_self_member@pulse.io", Roles.Engineer);
        var borrower = await SeedEngineerAsync("recall_self_borrower@pulse.io", Roles.Engineer);
        var teamA = await SeedTeamAsync("Recall Self Team A", lead.Id);
        var teamB = await SeedTeamAsync("Recall Self Team B");
        await AssignEngineerToTeamAsync(member.Id, teamA.Id);
        await AssignEngineerToTeamAsync(borrower.Id, teamB.Id);

        var project = await SeedProjectAsync("Recall self project");
        var task = await SeedTaskAsync("Task to self-recall", project.Id, assigneeId: member.Id);
        var leadClient = await AuthenticatedClientAsync("recall_self_lead@pulse.io");

        var loanResponse = await leadClient.PostAsJsonAsync(
            $"/api/v1/tasks/{task.Id}/loan",
            new { targetEngineerId = borrower.Id, reason = "Needed for sprint" });
        loanResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var borrowerClient = await AuthenticatedClientAsync("recall_self_borrower@pulse.io");
        var recallResponse = await borrowerClient.PostAsync($"/api/v1/tasks/{task.Id}/recall", null);

        recallResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await recallResponse.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        body!.Data!.AssigneeId.Should().Be(member.Id);
    }

    [Fact]
    public async Task RecallTask_returns_422_when_the_task_was_never_loaned()
    {
        var lead = await SeedEngineerAsync("recall_never_lead@pulse.io", Roles.TeamLead);
        var member = await SeedEngineerAsync("recall_never_member@pulse.io", Roles.Engineer);
        var teamA = await SeedTeamAsync("Recall Never Team", lead.Id);
        await AssignEngineerToTeamAsync(member.Id, teamA.Id);

        var project = await SeedProjectAsync("Recall never-loaned project");
        var task = await SeedTaskAsync("Never loaned task", project.Id, assigneeId: member.Id);
        var leadClient = await AuthenticatedClientAsync("recall_never_lead@pulse.io");

        var response = await leadClient.PostAsync($"/api/v1/tasks/{task.Id}/recall", null);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task RecallTask_returns_422_when_a_regular_reassignment_has_already_superseded_the_loan()
    {
        var lead = await SeedEngineerAsync("recall_super_lead@pulse.io", Roles.TeamLead);
        var member = await SeedEngineerAsync("recall_super_member@pulse.io", Roles.Engineer);
        var borrower = await SeedEngineerAsync("recall_super_borrower@pulse.io", Roles.Engineer);
        var teamA = await SeedTeamAsync("Recall Super Team A", lead.Id);
        var teamB = await SeedTeamAsync("Recall Super Team B");
        await AssignEngineerToTeamAsync(member.Id, teamA.Id);
        await AssignEngineerToTeamAsync(borrower.Id, teamB.Id);

        var project = await SeedProjectAsync("Recall superseded project");
        var task = await SeedTaskAsync("Task loaned then reassigned onward", project.Id, assigneeId: member.Id);
        var leadClient = await AuthenticatedClientAsync("recall_super_lead@pulse.io");

        await leadClient.PostAsJsonAsync(
            $"/api/v1/tasks/{task.Id}/loan",
            new { targetEngineerId = borrower.Id });

        // A department head reassigns it onward through the regular path before anyone recalls it.
        await SeedEngineerAsync("recall_super_head@pulse.io", Roles.HeadOfPmo);
        var headClient = await AuthenticatedClientAsync("recall_super_head@pulse.io");
        var elsewhere = await SeedEngineerAsync("recall_super_elsewhere@pulse.io", Roles.Engineer);
        await headClient.PatchAsJsonAsync($"/api/v1/tasks/{task.Id}", new { assigneeId = elsewhere.Id });

        var response = await leadClient.PostAsync($"/api/v1/tasks/{task.Id}/recall", null);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task RecallTask_returns_403_when_the_actor_did_not_lend_the_task()
    {
        var lead = await SeedEngineerAsync("recall_wronglead_lead@pulse.io", Roles.TeamLead);
        var member = await SeedEngineerAsync("recall_wronglead_member@pulse.io", Roles.Engineer);
        var borrower = await SeedEngineerAsync("recall_wronglead_borrower@pulse.io", Roles.Engineer);
        var teamA = await SeedTeamAsync("Recall WrongLead Team A", lead.Id);
        var teamB = await SeedTeamAsync("Recall WrongLead Team B");
        await AssignEngineerToTeamAsync(member.Id, teamA.Id);
        await AssignEngineerToTeamAsync(borrower.Id, teamB.Id);

        var project = await SeedProjectAsync("Recall wrong-lead project");
        var task = await SeedTaskAsync("Task loaned, wrong lead tries to recall", project.Id, assigneeId: member.Id);
        var leadClient = await AuthenticatedClientAsync("recall_wronglead_lead@pulse.io");

        await leadClient.PostAsJsonAsync(
            $"/api/v1/tasks/{task.Id}/loan",
            new { targetEngineerId = borrower.Id });

        var otherLead = await SeedEngineerAsync("recall_wronglead_other@pulse.io", Roles.TeamLead);
        await SeedTeamAsync("Recall WrongLead Other Team", otherLead.Id);
        var otherLeadClient = await AuthenticatedClientAsync("recall_wronglead_other@pulse.io");

        var response = await otherLeadClient.PostAsync($"/api/v1/tasks/{task.Id}/recall", null);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task RecallTask_allows_a_department_head_to_recall_a_task_they_did_not_personally_lend()
    {
        var lead = await SeedEngineerAsync("recall_head_lead@pulse.io", Roles.TeamLead);
        var member = await SeedEngineerAsync("recall_head_member@pulse.io", Roles.Engineer);
        var borrower = await SeedEngineerAsync("recall_head_borrower@pulse.io", Roles.Engineer);
        var teamA = await SeedTeamAsync("Recall Head Team A", lead.Id);
        var teamB = await SeedTeamAsync("Recall Head Team B");
        await AssignEngineerToTeamAsync(member.Id, teamA.Id);
        await AssignEngineerToTeamAsync(borrower.Id, teamB.Id);
        await SeedEngineerAsync("recall_head_head@pulse.io", Roles.HeadOfPmo);

        var project = await SeedProjectAsync("Recall head project");
        var task = await SeedTaskAsync("Task loaned, head recalls it", project.Id, assigneeId: member.Id);
        var leadClient = await AuthenticatedClientAsync("recall_head_lead@pulse.io");

        await leadClient.PostAsJsonAsync(
            $"/api/v1/tasks/{task.Id}/loan",
            new { targetEngineerId = borrower.Id });

        var headClient = await AuthenticatedClientAsync("recall_head_head@pulse.io");
        var response = await headClient.PostAsync($"/api/v1/tasks/{task.Id}/recall", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts);
        body!.Data!.AssigneeId.Should().Be(member.Id);
    }
}
