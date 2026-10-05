using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Projects;
using Pulse.Application.Tasks;
using Pulse.Domain.Engineers;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Pulse.Application.Common.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Tasks;

/// <summary>Personal tasks: HR and Accountant can keep private to-dos in a project of their own, to map
/// time to, without belonging to any real project.</summary>
[Collection("Integration")]
public class PersonalTasksTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public PersonalTasksTests(PulseWebApplicationFactory factory) : base(factory) { }

    private static string Day(int fromNow) => DateOnly.FromDateTime(DateTime.UtcNow.AddDays(fromNow)).ToString("yyyy-MM-dd");

    private async Task<TaskDto> CreatePersonalAsync(HttpClient client, string title, int dueInDays = 3)
    {
        var resp = await client.PostAsJsonAsync("/api/v1/tasks", new { title, personal = true, dueDate = Day(dueInDays) });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;
    }

    [Theory]
    [InlineData(Roles.HR)]
    [InlineData(Roles.Accountant)]
    public async Task HR_and_accountant_can_create_a_personal_task_without_any_project(string role)
    {
        var owner = await SeedEngineerAsync($"pt_{role}_create@pulse.io", role);
        var client = await AuthenticatedClientAsync($"pt_{role}_create@pulse.io");
        var someoneElse = await SeedEngineerAsync($"pt_{role}_other@pulse.io", Roles.Engineer);

        // Everything that makes no sense for a to-do is ignored: another assignee, an estimate.
        var resp = await client.PostAsJsonAsync("/api/v1/tasks", new
        {
            title = "Review leave policy", personal = true, dueDate = Day(4), points = 8, assigneeId = someoneElse.Id,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var task = (await resp.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;
        task.AssigneeId.Should().Be(owner.Id);
        task.Points.Should().Be(0);
        task.Status.Should().Be("active", "a personal task has no estimate to wait for, and must be loggable straight away");

        var detail = (await (await client.GetAsync($"/api/v1/tasks/{task.Id}")).Content
            .ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;
        detail.ProjectName.Should().Contain("Personal");
        detail.IsPersonal.Should().BeTrue("the UI leaves out estimates, hand-offs and blockers for a private to-do");

        var second = await CreatePersonalAsync(client, "Prepare onboarding pack");
        second.ProjectId.Should().Be(task.ProjectId, "one personal project per person, reused");
    }

    [Fact]
    public async Task Each_person_gets_their_own_personal_project()
    {
        await SeedEngineerAsync("pt_each_hr@pulse.io", Roles.HR);
        await SeedEngineerAsync("pt_each_acct@pulse.io", Roles.Accountant);
        var hr = await AuthenticatedClientAsync("pt_each_hr@pulse.io");
        var acct = await AuthenticatedClientAsync("pt_each_acct@pulse.io");

        var a = await CreatePersonalAsync(hr, "HR to-do");
        var b = await CreatePersonalAsync(acct, "Accounts to-do");

        a.ProjectId.Should().NotBe(b.ProjectId);
    }

    [Theory]
    [InlineData(Roles.Engineer)]
    [InlineData(Roles.TeamLead)]
    [InlineData(Roles.HeadOfPmo)]
    [InlineData(Roles.Executive)]
    public async Task Other_roles_cannot_create_personal_tasks(string role)
    {
        await SeedEngineerAsync($"pt_{role}_denied@pulse.io", role);
        var client = await AuthenticatedClientAsync($"pt_{role}_denied@pulse.io");

        var resp = await client.PostAsJsonAsync("/api/v1/tasks", new { title = "Nope", personal = true, dueDate = Day(3) });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_personal_project_never_appears_in_project_lists()
    {
        await SeedEngineerAsync("pt_lists_hr@pulse.io", Roles.HR);
        await SeedEngineerAsync("pt_lists_pmo@pulse.io", Roles.HeadOfPmo);
        var hr = await AuthenticatedClientAsync("pt_lists_hr@pulse.io");
        var pmo = await AuthenticatedClientAsync("pt_lists_pmo@pulse.io");
        var task = await CreatePersonalAsync(hr, "Private list check");

        foreach (var (client, url) in new[] { (hr, "/api/v1/projects"), (hr, "/api/v1/projects/mine"), (pmo, "/api/v1/projects") })
        {
            var body = await (await client.GetAsync(url)).Content.ReadAsStringAsync();
            body.Should().NotContain(task.ProjectId.ToString(), $"{url} must not list a personal project");
        }
    }

    [Fact]
    public async Task Personal_tasks_are_left_out_of_org_wide_task_lists_but_found_by_their_owner()
    {
        var owner = await SeedEngineerAsync("pt_tasklist_hr@pulse.io", Roles.HR);
        await SeedEngineerAsync("pt_tasklist_pmo@pulse.io", Roles.HeadOfPmo);
        var hr = await AuthenticatedClientAsync("pt_tasklist_hr@pulse.io");
        var pmo = await AuthenticatedClientAsync("pt_tasklist_pmo@pulse.io");
        var task = await CreatePersonalAsync(hr, "Visible only to me");

        async Task<List<Guid>> IdsAsync(HttpClient c, string url)
        {
            var body = await (await c.GetAsync(url)).Content.ReadFromJsonAsync<ApiResponse<PagedResult<TaskDto>>>(JsonOpts);
            return body!.Data!.Items.Select(t => t.Id).ToList();
        }

        (await IdsAsync(pmo, "/api/v1/tasks?limit=100")).Should().NotContain(task.Id);
        (await IdsAsync(hr, $"/api/v1/tasks?assigneeId={owner.Id}&limit=100")).Should().Contain(task.Id,
            "My tasks, My Time and the timer all list by assignee");
    }

    [Fact]
    public async Task Time_can_be_logged_against_a_personal_task_and_its_timer_started()
    {
        await SeedEngineerAsync("pt_time_hr@pulse.io", Roles.HR);
        var hr = await AuthenticatedClientAsync("pt_time_hr@pulse.io");
        var task = await CreatePersonalAsync(hr, "Draft the handbook update");

        var logged = await hr.PostAsJsonAsync("/api/v1/time-entries", new
        {
            date = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"), category = "task", taskId = task.Id, hours = 2,
        });
        logged.StatusCode.Should().Be(HttpStatusCode.OK);

        var timer = await hr.PostAsJsonAsync("/api/v1/time-entries/timer/start", new { category = "task", taskId = task.Id });
        timer.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Personal_tasks_are_excluded_from_the_shared_escalation_list_search_and_the_org_sweeps()
    {
        await SeedEngineerAsync("pt_sweeps_hr@pulse.io", Roles.HR);
        var assignee = await SeedEngineerAsync("pt_sweeps_eng@pulse.io", Roles.Engineer);
        var hr = await AuthenticatedClientAsync("pt_sweeps_hr@pulse.io");
        var personal = await CreatePersonalAsync(hr, "Overdue personal to-do zzqx", dueInDays: -2);
        var project = await SeedProjectAsync("Sweeps project");
        var ordinary = await SeedTaskAsync("Overdue ordinary task zzqx", project.Id, points: 3, dueDaysFromNow: -2, assigneeId: assignee.Id);

        using var scope = Factory.Services.CreateScope();
        var tasks = scope.ServiceProvider.GetRequiredService<ITaskRepository>();

        var candidates = await tasks.GetEscalationCandidatesAsync(3);
        candidates.Select(t => t.Id).Should().Contain(ordinary.Id).And.NotContain(personal.Id,
            "a personal to-do must never escalate to a lead or PMO");
        (await tasks.SearchAsync("zzqx", 20, Guid.NewGuid())).Select(t => t.Id).Should().Contain(ordinary.Id).And.NotContain(personal.Id,
            "someone else searching never finds it");
        (await tasks.GetPersonalEscalationCandidatesAsync(3)).Select(t => t.Id).Should().Contain(personal.Id,
            "the scanner reads personal tasks from their own query, to remind the owner only");
        (await tasks.GetAllActiveAsync()).Select(t => t.Id).Should().NotContain(personal.Id);
    }

    [Theory]
    [InlineData(Roles.HeadOfPmo)]
    [InlineData(Roles.ProjectManager)]
    [InlineData(Roles.HeadOfProduct)]
    [InlineData(Roles.HeadOfRnD)]
    [InlineData(Roles.TeamLead)]
    public async Task Nobody_but_the_owner_can_open_a_personal_task_not_even_org_wide_roles(string role)
    {
        await SeedEngineerAsync($"pt_open_{role}_hr@pulse.io", Roles.HR);
        await SeedEngineerAsync($"pt_open_{role}@pulse.io", role);
        var hr = await AuthenticatedClientAsync($"pt_open_{role}_hr@pulse.io");
        var other = await AuthenticatedClientAsync($"pt_open_{role}@pulse.io");
        var task = await CreatePersonalAsync(hr, "Mine alone");

        (await hr.GetAsync($"/api/v1/tasks/{task.Id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await other.GetAsync($"/api/v1/tasks/{task.Id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await other.GetAsync($"/api/v1/projects/{task.ProjectId}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(Roles.HeadOfPmo)]
    [InlineData(Roles.ProjectManager)]
    [InlineData(Roles.TeamLead)]
    public async Task Filtering_the_task_list_by_the_owner_or_their_project_does_not_reveal_a_personal_task(string role)
    {
        var owner = await SeedEngineerAsync($"pt_filter_{role}_hr@pulse.io", Roles.HR);
        await SeedEngineerAsync($"pt_filter_{role}@pulse.io", role);
        var hr = await AuthenticatedClientAsync($"pt_filter_{role}_hr@pulse.io");
        var other = await AuthenticatedClientAsync($"pt_filter_{role}@pulse.io");
        var task = await CreatePersonalAsync(hr, "Not in anyone else's list");

        async Task<List<Guid>> IdsAsync(HttpClient c, string url)
        {
            var body = await (await c.GetAsync(url)).Content.ReadFromJsonAsync<ApiResponse<PagedResult<TaskDto>>>(JsonOpts);
            return body!.Data?.Items.Select(t => t.Id).ToList() ?? [];
        }

        (await IdsAsync(hr, $"/api/v1/tasks?assigneeId={owner.Id}&limit=100")).Should().Contain(task.Id);
        (await IdsAsync(other, $"/api/v1/tasks?assigneeId={owner.Id}&limit=100")).Should().NotContain(task.Id);
        (await IdsAsync(other, $"/api/v1/tasks?projectId={task.ProjectId}&limit=100")).Should().NotContain(task.Id);
    }

    [Fact]
    public async Task An_unrelated_engineer_cannot_open_someone_elses_personal_task()
    {
        await SeedEngineerAsync("pt_priv_hr@pulse.io", Roles.HR);
        await SeedEngineerAsync("pt_priv_eng@pulse.io", Roles.Engineer);
        var hr = await AuthenticatedClientAsync("pt_priv_hr@pulse.io");
        var eng = await AuthenticatedClientAsync("pt_priv_eng@pulse.io");
        var task = await CreatePersonalAsync(hr, "Not for engineers");

        (await eng.GetAsync($"/api/v1/tasks/{task.Id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_owner_finds_their_own_personal_task_in_search_and_nobody_else_does()
    {
        await SeedEngineerAsync("pt_search_hr@pulse.io", Roles.HR);
        await SeedEngineerAsync("pt_search_pmo@pulse.io", Roles.HeadOfPmo);
        await SeedEngineerAsync("pt_search_hr2@pulse.io", Roles.HR);
        var owner = await AuthenticatedClientAsync("pt_search_hr@pulse.io");
        await CreatePersonalAsync(owner, "Quarterly benefits audit qbxr");

        async Task<int> HitsAsync(HttpClient c)
        {
            var body = await (await c.GetAsync("/api/v1/search?q=qbxr")).Content
                .ReadFromJsonAsync<ApiResponse<Pulse.Application.Search.SearchResultDto>>(JsonOpts);
            return body!.Data!.Tasks.Count;
        }

        (await HitsAsync(owner)).Should().Be(1);
        (await HitsAsync(await AuthenticatedClientAsync("pt_search_pmo@pulse.io"))).Should().Be(0,
            "PMO can open a personal task by link, but it does not surface in their search");
        (await HitsAsync(await AuthenticatedClientAsync("pt_search_hr2@pulse.io"))).Should().Be(0);
    }

    [Fact]
    public async Task An_overdue_personal_task_reminds_its_owner_and_nobody_else()
    {
        var owner = await SeedEngineerAsync("pt_remind_hr@pulse.io", Roles.HR);
        var pmo = await SeedEngineerAsync("pt_remind_pmo@pulse.io", Roles.HeadOfPmo);
        var pm = await SeedEngineerAsync("pt_remind_pm@pulse.io", Roles.ProjectManager);
        var ownerClient = await AuthenticatedClientAsync("pt_remind_hr@pulse.io");
        var task = await CreatePersonalAsync(ownerClient, "Send the policy reminder", dueInDays: -2);

        // A task activated moments ago is inside the scanner's grace window; age it so it is plainly overdue.
        using (var aging = Factory.Services.CreateScope())
        {
            var agingDb = aging.ServiceProvider.GetRequiredService<Pulse.Infrastructure.Persistence.PulseDbContext>();
            await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRawAsync(
                agingDb.Database, "UPDATE tasks SET activated_at = now() - interval '12 days' WHERE id = {0}", task.Id);
        }

        using (var scope = Factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<Pulse.Application.Escalations.EscalationScanner>().RunAsync();

        using var check = Factory.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<Pulse.Infrastructure.Persistence.PulseDbContext>();
        var concerned = new[] { owner.Id, pmo.Id, pm.Id };
        var forThisTask = (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
                db.Notifications.Where(n => concerned.Contains(n.UserId))))
            .Where(n => n.Payload != null && n.Payload.Contains(task.Id.ToString())).ToList();

        forThisTask.Should().ContainSingle(n => n.UserId == owner.Id,
            "the owner gets the same reminder an engineer gets for their own overdue task");
        forThisTask.Should().NotContain(n => n.UserId == pmo.Id || n.UserId == pm.Id,
            "no PM/department-head overdue broadcast for someone's private to-do");
        forThisTask.Should().HaveCount(1);
    }
}
