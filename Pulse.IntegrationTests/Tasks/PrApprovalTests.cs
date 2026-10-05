using System.Net;
using System.Net.Http.Json;
using Pulse.Application.Common;
using Pulse.Application.Notifications;
using Pulse.Application.Tasks;
using Pulse.Domain.Engineers;
using Pulse.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace Pulse.IntegrationTests.Tasks;

[Collection("Integration")]
public class PrApprovalTests : IntegrationTestBase, IClassFixture<PulseWebApplicationFactory>
{
    public PrApprovalTests(PulseWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task Assignee_requests_approval_and_department_head_approves_it_unblocking_MarkDone()
    {
        var team = await SeedTeamAsync("PR Approval Team", department: "Engineering");
        var head = await SeedEngineerAsync("pr_approve_head@pulse.io", Roles.HeadOfRnD);
        await AssignEngineerToTeamAsync(head.Id, team.Id);
        var assignee = await SeedEngineerAsync("pr_approve_assignee@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(assignee.Id, team.Id);
        var project = await SeedProjectAsync("PR Approval project", ownerTeamId: team.Id);
        var task = await SeedTaskAsync("Needs PR approval", project.Id, assigneeId: assignee.Id, requiresPrApproval: true);

        var assigneeClient = await AuthenticatedClientAsync("pr_approve_assignee@pulse.io");
        var requestResponse = await assigneeClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/pr-approval/request",
            new { prLink = "https://bitbucket.org/org/repo/pull-requests/1" });
        requestResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Mark Done is blocked while the request is pending.
        var blockedDone = await assigneeClient.PostAsync($"/api/v1/tasks/{task.Id}/mark-done", null);
        blockedDone.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var headClient = await AuthenticatedClientAsync("pr_approve_head@pulse.io");
        var headNotifResponse = await headClient.GetAsync("/api/v1/notifications");
        var headNotifications = (await headNotifResponse.Content.ReadFromJsonAsync<ApiResponse<PagedResult<NotificationDto>>>(JsonOpts))!.Data!;
        headNotifications.Items.Should().Contain(n => n.Kind == "pr_approval_requested");

        var approveResponse = await headClient.PostAsync($"/api/v1/tasks/{task.Id}/pr-approval/approve", null);
        approveResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var approved = (await approveResponse.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;
        approved.PrApprovedAt.Should().NotBeNull();

        var doneResponse = await assigneeClient.PostAsync($"/api/v1/tasks/{task.Id}/mark-done", null);
        doneResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var done = (await doneResponse.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;
        done.Status.Should().Be("done");

        var assigneeNotifResponse = await assigneeClient.GetAsync("/api/v1/notifications");
        var assigneeNotifications = (await assigneeNotifResponse.Content.ReadFromJsonAsync<ApiResponse<PagedResult<NotificationDto>>>(JsonOpts))!.Data!;
        assigneeNotifications.Items.Should().Contain(n => n.Kind == "pr_approval_approved");
    }

    [Fact]
    public async Task Rejection_clears_the_pending_request_and_PR_link_so_the_assignee_resubmits()
    {
        var team = await SeedTeamAsync("PR Reject Team", department: "Engineering");
        var head = await SeedEngineerAsync("pr_reject_head@pulse.io", Roles.HeadOfRnD);
        await AssignEngineerToTeamAsync(head.Id, team.Id);
        var assignee = await SeedEngineerAsync("pr_reject_assignee@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(assignee.Id, team.Id);
        var project = await SeedProjectAsync("PR Reject project", ownerTeamId: team.Id);
        var task = await SeedTaskAsync("Needs PR approval", project.Id, assigneeId: assignee.Id, requiresPrApproval: true);

        var assigneeClient = await AuthenticatedClientAsync("pr_reject_assignee@pulse.io");
        await assigneeClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/pr-approval/request",
            new { prLink = "https://bitbucket.org/org/repo/pull-requests/1" });

        var headClient = await AuthenticatedClientAsync("pr_reject_head@pulse.io");
        var rejectResponse = await headClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/pr-approval/reject",
            new { reason = "needs more tests" });
        rejectResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var rejected = (await rejectResponse.Content.ReadFromJsonAsync<ApiResponse<TaskDto>>(JsonOpts))!.Data!;
        rejected.PrLink.Should().BeNull();
        rejected.PendingPrApprovalRequestedAt.Should().BeNull();

        // Resubmitting with a fresh link works — nothing left over from the rejected request.
        var resubmitResponse = await assigneeClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/pr-approval/request",
            new { prLink = "https://bitbucket.org/org/repo/pull-requests/2" });
        resubmitResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Department_head_reassigns_to_another_engineer_who_can_then_approve_instead()
    {
        var team = await SeedTeamAsync("PR Reassign Team", department: "Engineering");
        var head = await SeedEngineerAsync("pr_reassign_head@pulse.io", Roles.HeadOfRnD);
        await AssignEngineerToTeamAsync(head.Id, team.Id);
        var assignee = await SeedEngineerAsync("pr_reassign_assignee@pulse.io", Roles.Engineer);
        await AssignEngineerToTeamAsync(assignee.Id, team.Id);
        var delegateLead = await SeedEngineerAsync("pr_reassign_delegate@pulse.io", Roles.TeamLead);
        var project = await SeedProjectAsync("PR Reassign project", ownerTeamId: team.Id);
        var task = await SeedTaskAsync("Needs PR approval", project.Id, assigneeId: assignee.Id, requiresPrApproval: true);

        var assigneeClient = await AuthenticatedClientAsync("pr_reassign_assignee@pulse.io");
        await assigneeClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/pr-approval/request",
            new { prLink = "https://bitbucket.org/org/repo/pull-requests/1" });

        var headClient = await AuthenticatedClientAsync("pr_reassign_head@pulse.io");
        var reassignResponse = await headClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/pr-approval/reassign",
            new { approverId = delegateLead.Id });
        reassignResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // The original head can no longer approve once reassigned away from them.
        var headApproveAttempt = await headClient.PostAsync($"/api/v1/tasks/{task.Id}/pr-approval/approve", null);
        headApproveAttempt.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var delegateNotifResponse = await (await AuthenticatedClientAsync("pr_reassign_delegate@pulse.io")).GetAsync("/api/v1/notifications");
        var delegateNotifications = (await delegateNotifResponse.Content.ReadFromJsonAsync<ApiResponse<PagedResult<NotificationDto>>>(JsonOpts))!.Data!;
        delegateNotifications.Items.Should().Contain(n => n.Kind == "pr_approval_reassigned");

        // The delegate has no team, no project membership, and no other standing access to this
        // project — reassignment must grant them enough to actually open the task, or handing them
        // the approval would be pointless. Verified in the browser: without this, the delegate's
        // own GetTaskQuery call 403s before they ever see the Approve button.
        var delegateClient = await AuthenticatedClientAsync("pr_reassign_delegate@pulse.io");
        var delegateGetResponse = await delegateClient.GetAsync($"/api/v1/tasks/{task.Id}");
        delegateGetResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var delegateApproveResponse = await delegateClient.PostAsync($"/api/v1/tasks/{task.Id}/pr-approval/approve", null);
        delegateApproveResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Head_of_Pmo_can_reassign_a_pending_request_even_without_being_the_resolved_head()
    {
        var assignee = await SeedEngineerAsync("pr_pmo_assignee@pulse.io", Roles.Engineer); // no team — no department head resolves
        await SeedEngineerAsync("pr_pmo_head@pulse.io", Roles.HeadOfPmo);
        var project = await SeedProjectAsync("PR Pmo Reassign project");
        var task = await SeedTaskAsync("Needs PR approval", project.Id, assigneeId: assignee.Id, requiresPrApproval: true);

        var assigneeClient = await AuthenticatedClientAsync("pr_pmo_assignee@pulse.io");
        await assigneeClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/pr-approval/request",
            new { prLink = "https://bitbucket.org/org/repo/pull-requests/1" });

        var delegateLead = await SeedEngineerAsync("pr_pmo_delegate@pulse.io", Roles.TeamLead);
        var pmoClient = await AuthenticatedClientAsync("pr_pmo_head@pulse.io");
        var reassignResponse = await pmoClient.PostAsJsonAsync($"/api/v1/tasks/{task.Id}/pr-approval/reassign",
            new { approverId = delegateLead.Id });

        reassignResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
