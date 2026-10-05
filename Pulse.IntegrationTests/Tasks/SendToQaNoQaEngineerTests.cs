using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Tasks;
using Pulse.Domain.Engineers;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace Pulse.IntegrationTests.Tasks;

/// <summary>
/// Isolated in its own test class, with its own fresh Testcontainers Postgres instance — the one
/// assertion here ("stays unassigned") requires that literally no active QA engineer exists
/// anywhere in the database, which FindQaEngineerAsync's org-wide fallback tier now genuinely
/// searches. Sharing a class (and its database) with any other test that seeds a QA engineer
/// would make this test depend on execution order, since that engineer would leak in as a match.
/// </summary>
[Collection("Integration")]
public class SendToQaNoQaEngineerTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public SendToQaNoQaEngineerTests(PulseWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task SendToQa_with_no_available_QA_engineer_anywhere_leaves_the_QA_task_unassigned()
    {
        var engineer = await SeedEngineerAsync("qa_no_reviewer@pulse.io", Roles.Engineer);
        var project = await SeedProjectAsync("QA no-reviewer project");
        var task = await SeedTaskAsync("Needs QA", project.Id, assigneeId: engineer.Id, requiresQa: true);
        var client = await AuthenticatedClientAsync("qa_no_reviewer@pulse.io");

        var response = await client.PostAsync($"/api/v1/tasks/{task.Id}/send-to-qa", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = (await response.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;
        updated.QaTaskId.Should().NotBeNull();

        var qaTaskResponse = await client.GetAsync($"/api/v1/tasks/{updated.QaTaskId}");
        var qaTask = (await qaTaskResponse.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;
        qaTask.AssigneeId.Should().BeNull();
    }
}
