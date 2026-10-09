using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Tasks;
using Pulse.Domain.Engineers;
using Pulse.Infrastructure.Persistence;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Pulse.IntegrationTests.Tasks;

/// <summary>
/// A task In QA waits on its QA task. If that task is deleted, nothing can accept or reject the parent and it is stuck: it cannot be edited
/// (In QA) and cannot be closed (nobody can do the review). Deleting must not create that state, and where it already exists (qa_task_id is a
/// plain column, so the database never objected) there has to be a way back.
/// </summary>
[Collection("Integration")]
public class MissingQaTaskTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public MissingQaTaskTests(PulseWebApplicationFactory factory) : base(factory) { }

    private sealed record Setup(HttpClient Pmo, HttpClient Engineer, Guid ParentId, Guid QaTaskId);

    /// <summary>A task sent to QA through the API, so the QA task and the link are exactly what production creates.</summary>
    private async Task<Setup> SendTaskToQaAsync(string tag)
    {
        var pmo = await SeedEngineerAsync($"mq_pmo_{tag}@pulse.io", Roles.HeadOfPmo);
        var engineer = await SeedEngineerAsync($"mq_eng_{tag}@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync($"Missing QA {tag}");
        await SeedProjectMemberAsync(project.Id, engineer.Id);
        var task = await SeedTaskAsync($"Feature {tag}", project.Id, assigneeId: engineer.Id, requiresQa: true);

        var engineerClient = await AuthenticatedClientAsync($"mq_eng_{tag}@pulse.io");
        var sent = await engineerClient.PostAsync($"/api/v1/tasks/{task.Id}/send-to-qa", null);
        sent.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await sent.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;
        dto.QaTaskId.Should().NotBeNull();

        return new Setup(await AuthenticatedClientAsync($"mq_pmo_{tag}@pulse.io"), engineerClient, task.Id, dto.QaTaskId!.Value);
    }

    private async Task<TaskDto> GetAsync(HttpClient client, Guid id) =>
        (await (await client.GetAsync($"/api/v1/tasks/{id}")).Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;

    /// <summary>What a delete used to leave behind (and what already exists in the data): the QA row gone, the parent still pointing at it.</summary>
    private async Task RemoveQaTaskRowDirectlyAsync(Guid qaTaskId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PulseDbContext>();
        await db.Tasks.Where(t => t.Id == qaTaskId).ExecuteDeleteAsync();
    }

    [Fact]
    public async Task Deleting_the_QA_task_sends_the_parent_back_to_Active_with_the_link_cleared()
    {
        var s = await SendTaskToQaAsync("del");

        (await s.Pmo.DeleteAsync($"/api/v1/tasks/{s.QaTaskId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var parent = await GetAsync(s.Pmo, s.ParentId);
        parent.Status.Should().Be("active");
        parent.QaTaskId.Should().BeNull();
        parent.QaTaskMissing.Should().BeFalse();
    }

    [Fact]
    public async Task A_task_whose_QA_task_was_deleted_can_be_sent_to_QA_again()
    {
        var s = await SendTaskToQaAsync("resend");
        await s.Pmo.DeleteAsync($"/api/v1/tasks/{s.QaTaskId}");

        var resent = await s.Engineer.PostAsync($"/api/v1/tasks/{s.ParentId}/send-to-qa", null);

        resent.StatusCode.Should().Be(HttpStatusCode.OK);
        var parent = await GetAsync(s.Pmo, s.ParentId);
        parent.Status.Should().Be("inQa");
        parent.QaTaskId.Should().NotBeNull().And.NotBe(s.QaTaskId);
        (await s.Pmo.GetAsync($"/api/v1/tasks/{parent.QaTaskId}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Deleting_a_task_takes_its_QA_task_with_it()
    {
        var s = await SendTaskToQaAsync("cascade");

        (await s.Pmo.DeleteAsync($"/api/v1/tasks/{s.ParentId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await s.Pmo.GetAsync($"/api/v1/tasks/{s.QaTaskId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_dangling_QA_link_is_flagged_on_the_task()
    {
        var s = await SendTaskToQaAsync("flag");
        (await GetAsync(s.Pmo, s.ParentId)).QaTaskMissing.Should().BeFalse("its QA task exists");

        await RemoveQaTaskRowDirectlyAsync(s.QaTaskId);

        var parent = await GetAsync(s.Pmo, s.ParentId);
        parent.Status.Should().Be("inQa");
        parent.QaTaskMissing.Should().BeTrue();
    }

    [Fact]
    public async Task A_stuck_task_can_be_recovered_and_then_sent_to_QA_again()
    {
        var s = await SendTaskToQaAsync("recover");
        await RemoveQaTaskRowDirectlyAsync(s.QaTaskId);

        var recovered = await s.Pmo.PostAsync($"/api/v1/tasks/{s.ParentId}/recover-missing-qa", null);

        recovered.StatusCode.Should().Be(HttpStatusCode.OK);
        var parent = await GetAsync(s.Pmo, s.ParentId);
        parent.Status.Should().Be("active");
        parent.QaTaskId.Should().BeNull();
        parent.QaTaskMissing.Should().BeFalse();

        (await s.Engineer.PostAsync($"/api/v1/tasks/{s.ParentId}/send-to-qa", null)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_assignee_can_recover_their_own_stuck_task()
    {
        var s = await SendTaskToQaAsync("assignee");
        await RemoveQaTaskRowDirectlyAsync(s.QaTaskId);

        (await s.Engineer.PostAsync($"/api/v1/tasks/{s.ParentId}/recover-missing-qa", null)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Recovery_is_refused_while_the_QA_task_still_exists()
    {
        var s = await SendTaskToQaAsync("exists");

        var response = await s.Pmo.PostAsync($"/api/v1/tasks/{s.ParentId}/recover-missing-qa", null);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await GetAsync(s.Pmo, s.ParentId)).Status.Should().Be("inQa", "QA, not this endpoint, decides a task that is waiting on a real review");
    }

    [Fact]
    public async Task Recovery_is_refused_for_a_task_that_is_not_in_QA()
    {
        var s = await SendTaskToQaAsync("notinqa");
        await s.Pmo.DeleteAsync($"/api/v1/tasks/{s.QaTaskId}");   // parent is Active again

        (await s.Pmo.PostAsync($"/api/v1/tasks/{s.ParentId}/recover-missing-qa", null)).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }
}
