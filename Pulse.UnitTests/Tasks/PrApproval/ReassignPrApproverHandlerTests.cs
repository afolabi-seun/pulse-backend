using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Tasks.Commands;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using Pulse.Domain.Teams;
using FluentAssertions;
using Moq;
using Pulse.UnitTests.Common;

namespace Pulse.UnitTests.Tasks.PrApproval;

public class ReassignPrApproverHandlerTests
{
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<IProjectRepository> _projects = new();
    private readonly Mock<IAuditLogRepository> _audit = new();
    private readonly Mock<INotificationRepository> _notifications = new();
    private readonly Mock<IRealtimeNotifier> _realtime = new();
    private readonly Mock<IEmailQueue> _emailQueue = new();
    private readonly Mock<IAppSettings> _settings = new();

    public ReassignPrApproverHandlerTests()
    {
        _settings.Setup(s => s.AppBaseUrl).Returns("https://pulse.test");
        _teams.Setup(t => t.ListAllAsync(default)).ReturnsAsync([]);
        _engineers.Setup(e => e.ListActiveAsync(default)).ReturnsAsync([]);
    }

    private ReassignPrApproverHandler CreateHandler() => new(
        _tasks.Object, _engineers.Object, _teams.Object, _projects.Object, _audit.Object,
        _settings.Object, TestNotifications.Dispatcher(_notifications, _realtime, _emailQueue));

    private (Engineer assignee, Engineer head, PulseTask task) SetUpPendingRequest()
    {
        var team = Team.Create("Platform");
        team.SetDepartment("Engineering");
        var assignee = Engineer.Create("Dev", "dev@x.io", "hash", Roles.Engineer, 20, 14);
        assignee.AssignToTeam(team.Id);
        var head = Engineer.Create("Head", "head@x.io", "hash", Roles.HeadOfRnD, 20, 14);
        head.AssignToTeam(team.Id);

        var task = PulseTask.Create("Task", 3, Guid.NewGuid());
        task.Assign(assignee.Id, Guid.NewGuid());
        task.SetRequiresPrApproval(true);
        task.RequestPrApproval("https://x/pr/1", Guid.NewGuid());

        _tasks.Setup(t => t.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _teams.Setup(r => r.GetByIdAsync(team.Id, default)).ReturnsAsync(team);
        _teams.Setup(r => r.ListAllAsync(default)).ReturnsAsync([team]);
        _engineers.Setup(r => r.GetByIdAsync(assignee.Id, default)).ReturnsAsync(assignee);
        _engineers.Setup(r => r.GetByIdAsync(head.Id, default)).ReturnsAsync(head);
        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync([assignee, head]);

        return (assignee, head, task);
    }

    [Fact]
    public async Task Fails_with_FORBIDDEN_for_someone_who_is_not_the_resolved_department_head()
    {
        var (_, _, task) = SetUpPendingRequest();
        var newApprover = Engineer.Create("Other Head", "other@x.io", "hash", Roles.HeadOfInfraDevOps, 20, 14);
        _engineers.Setup(r => r.GetByIdAsync(newApprover.Id, default)).ReturnsAsync(newApprover);

        var result = await CreateHandler().Handle(
            new ReassignPrApproverCommand(task.Id, newApprover.Id, Guid.NewGuid(), null, Roles.Engineer), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
    }

    [Fact]
    public async Task Department_head_can_reassign_to_another_eligible_engineer_and_they_are_notified()
    {
        var (_, head, task) = SetUpPendingRequest();
        var newApprover = Engineer.Create("Other Head", "other@x.io", "hash", Roles.HeadOfInfraDevOps, 20, 14);
        _engineers.Setup(r => r.GetByIdAsync(newApprover.Id, default)).ReturnsAsync(newApprover);

        var result = await CreateHandler().Handle(
            new ReassignPrApproverCommand(task.Id, newApprover.Id, head.Id, null, Roles.HeadOfRnD), default);

        result.IsSuccess.Should().BeTrue();
        task.PendingPrApprovalDelegatedToEngineerId.Should().Be(newApprover.Id);
        _notifications.Verify(n => n.AddAsync(It.Is<Domain.Notifications.Notification>(x => x.UserId == newApprover.Id), default), Times.Once);
        // A reassignment can cross project boundaries — the delegate needs to actually be able to
        // open the task to act on it, not just pass PrApprovalPolicy's own authorization check.
        _projects.Verify(p => p.AddMemberAsync(task.ProjectId, newApprover.Id, default), Times.Once);
    }

    [Fact]
    public async Task Fails_when_the_target_is_below_TeamLead()
    {
        var (_, head, task) = SetUpPendingRequest();
        var plainEngineer = Engineer.Create("Plain Engineer", "plain@x.io", "hash", Roles.Engineer, 20, 14);
        _engineers.Setup(r => r.GetByIdAsync(plainEngineer.Id, default)).ReturnsAsync(plainEngineer);

        var result = await CreateHandler().Handle(
            new ReassignPrApproverCommand(task.Id, plainEngineer.Id, head.Id, null, Roles.HeadOfRnD), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
    }

    [Fact]
    public async Task Head_of_Pmo_can_reassign_even_when_not_the_resolved_head()
    {
        var (_, _, task) = SetUpPendingRequest();
        var newApprover = Engineer.Create("Other Head", "other@x.io", "hash", Roles.HeadOfInfraDevOps, 20, 14);
        _engineers.Setup(r => r.GetByIdAsync(newApprover.Id, default)).ReturnsAsync(newApprover);

        var result = await CreateHandler().Handle(
            new ReassignPrApproverCommand(task.Id, newApprover.Id, Guid.NewGuid(), null, Roles.HeadOfPmo), default);

        result.IsSuccess.Should().BeTrue();
    }
}
