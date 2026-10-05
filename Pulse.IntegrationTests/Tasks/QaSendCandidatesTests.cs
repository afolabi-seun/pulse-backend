using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Engineers.Queries;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Tasks;

/// <summary>The "Send to QA" reviewer picker must work for the task's own assignee, not only
/// PM-or-above — the engineer sending their own work to QA is exactly who needs to pick who
/// reviews it. See GetQaSendCandidatesQuery's own doc comment for the full reasoning.</summary>
[Collection("Integration")]
public class QaSendCandidatesTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public QaSendCandidatesTests(PulseWebApplicationFactory factory) : base(factory) { }

    private async Task SetDisciplineAsync(Guid engineerId, Discipline discipline)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        (await db.Engineers.FindAsync(engineerId))!.SetDiscipline(discipline);
        await db.SaveChangesAsync();
    }

    private async Task<QaSendCandidatesDto> GetCandidatesAsync(HttpClient client, Guid taskId)
    {
        var resp = await client.GetAsync($"/api/v1/tasks/{taskId}/qa-send-candidates");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await resp.Content.ReadFromJsonAsync<ApiResponse<QaSendCandidatesDto>>(JsonOpts))!.Data!;
    }

    [Fact]
    public async Task Plain_assignee_can_see_candidates_not_just_PM_or_above()
    {
        var owner = await SeedEngineerAsync("qsc_owner@pulse.io", Roles.Engineer);
        var reviewer = await SeedEngineerAsync("qsc_reviewer@pulse.io", Roles.Engineer, isQa: true);
        var project = await SeedProjectAsync("QA send candidates project");
        await SeedProjectMemberAsync(project.Id, reviewer.Id);
        var task = await SeedTaskAsync("Needs QA", project.Id, assigneeId: owner.Id, requiresQa: true);

        var client = await AuthenticatedClientAsync("qsc_owner@pulse.io");
        var candidates = await GetCandidatesAsync(client, task.Id);

        candidates.Candidates.Should().Contain(c => c.Id == reviewer.Id);
    }

    [Fact]
    public async Task Candidates_mark_project_members_first_and_exclude_non_QA_and_inactive_engineers()
    {
        var owner = await SeedEngineerAsync("qsc_mark_owner@pulse.io", Roles.Engineer);
        var onProject = await SeedEngineerAsync("qsc_mark_onproject@pulse.io", Roles.Engineer, isQa: true);
        var elsewhere = await SeedEngineerAsync("qsc_mark_elsewhere@pulse.io", Roles.Engineer, isQa: true);
        var notQa = await SeedEngineerAsync("qsc_mark_notqa@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("QA send marking project");
        await SeedProjectMemberAsync(project.Id, onProject.Id);
        var task = await SeedTaskAsync("Needs QA", project.Id, assigneeId: owner.Id, requiresQa: true);

        var client = await AuthenticatedClientAsync("qsc_mark_owner@pulse.io");
        var candidates = await GetCandidatesAsync(client, task.Id);

        candidates.Candidates.Single(c => c.Id == onProject.Id).OnProject.Should().BeTrue();
        candidates.Candidates.Single(c => c.Id == elsewhere.Id).OnProject.Should().BeFalse();
        candidates.Candidates.Should().NotContain(c => c.Id == notQa.Id);
    }

    [Fact]
    public async Task RecommendedEngineerId_matches_the_discipline_matched_on_project_reviewer()
    {
        var owner = await SeedEngineerAsync("qsc_rec_owner@pulse.io", Roles.Engineer);
        var wrongDiscipline = await SeedEngineerAsync("qsc_rec_wrong@pulse.io", Roles.Engineer, isQa: true);
        var rightDiscipline = await SeedEngineerAsync("qsc_rec_right@pulse.io", Roles.Engineer, isQa: true);
        var project = await SeedProjectAsync("QA send recommend project");
        await SeedProjectMemberAsync(project.Id, wrongDiscipline.Id);
        await SeedProjectMemberAsync(project.Id, rightDiscipline.Id);
        var task = await SeedTaskAsync("Needs QA", project.Id, assigneeId: owner.Id, requiresQa: true);

        // Deliberately an uncommon discipline pair in this suite (not Backend/Frontend) — these
        // engineers stay active in the shared integration DB after this test, and the org-wide
        // tiers of other SendToQa tests assume they're the only Backend/Frontend QA match around.
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
            (await db.Tasks.FindAsync(task.Id))!.SetDiscipline(Discipline.Design);
            await db.SaveChangesAsync();
        }
        await SetDisciplineAsync(wrongDiscipline.Id, Discipline.Database);
        await SetDisciplineAsync(rightDiscipline.Id, Discipline.Design);

        var client = await AuthenticatedClientAsync("qsc_rec_owner@pulse.io");
        var candidates = await GetCandidatesAsync(client, task.Id);

        candidates.RecommendedEngineerId.Should().Be(rightDiscipline.Id);
    }

    [Fact]
    public async Task Engineer_with_no_access_to_the_task_is_forbidden()
    {
        var owner = await SeedEngineerAsync("qsc_owner_private@pulse.io", Roles.Engineer);
        await SeedEngineerAsync("qsc_outsider@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("QA send private project");
        var task = await SeedTaskAsync("Needs QA", project.Id, assigneeId: owner.Id, requiresQa: true);

        var client = await AuthenticatedClientAsync("qsc_outsider@pulse.io");
        var resp = await client.GetAsync($"/api/v1/tasks/{task.Id}/qa-send-candidates");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
