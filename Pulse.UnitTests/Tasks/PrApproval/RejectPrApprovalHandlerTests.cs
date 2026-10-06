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

public class RejectPrApprovalHandlerTests
{
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<IAuditLogRepository> _audit = new();
    private readonly Mock<INotificationRepository> _notifications = new();
    private readonly Mock<IRealtimeNotifier> _realtime = new();
    private readonly Mock<IEmailQueue> _emailQueue = new();
    private readonly Mock<IAppSettings> _settings = new();

    public RejectPrApprovalHandlerTests()
    {
        _settings.Setup(s => s.AppBaseUrl).Returns("https://pulse.test");
        _teams.Setup(t => t.ListAllAsync(default)).ReturnsAsync([]);
        _engineers.Setup(e => e.ListActiveAsync(default)).ReturnsAsync([]);
    }

    private RejectPrApprovalHandler CreateHandler() => new(
        _tasks.Object, _engineers.Object, _teams.Object, _audit.Object,
        _settings.Object, TestNotifications.Dispatcher(_notifications, _realtime, _emailQueue));

    private (Engineer assignee, Engineer head, PulseTask task, Guid requesterId) SetUpPendingRequest()
    {
        var team = Team.Create("Platform");
        team.SetDepartment("Engineering");
        var assignee = Engineer.Create("Dev", "dev@x.io", "hash", Roles.Engineer, 20, 14);
        assignee.AssignToTeam(team.Id);
        var head = Engineer.Create("Head", "head@x.io", "hash", Roles.HeadOfRnD, 20, 14);
        head.AssignToTeam(team.Id);
        var requesterId = Guid.NewGuid();

        var task = PulseTask.Create("Task", 3, Guid.NewGuid());
        task.Assign(assignee.Id, Guid.NewGuid());
        task.SetRequiresPrApproval(true);
        task.RequestPrApproval("https://x/pr/1", requesterId);

        _tasks.Setup(t => t.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _teams.Setup(r => r.GetByIdAsync(team.Id, default)).ReturnsAsync(team);
        _teams.Setup(r => r.ListAllAsync(default)).ReturnsAsync([team]);
        _engineers.Setup(r => r.GetByIdAsync(assignee.Id, default)).ReturnsAsync(assignee);
        _engineers.Setup(r => r.GetByIdAsync(head.Id, default)).ReturnsAsync(head);
        _engineers.Setup(r => r.GetByIdAsync(requesterId, default)).ReturnsAsync(assignee);
        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync([assignee, head]);

        return (assignee, head, task, requesterId);
    }

    [Fact]
    public async Task Fails_with_NOT_PENDING_when_nothing_is_awaiting_approval()
    {
        var task = PulseTask.Create("Task", 3, Guid.NewGuid());
        _tasks.Setup(t => t.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateHandler().Handle(new RejectPrApprovalCommand(task.Id, null, Guid.NewGuid(), null, Roles.HeadOfRnD), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_PENDING");
    }

    [Fact]
    public async Task Fails_with_FORBIDDEN_for_someone_who_is_not_the_resolved_department_head()
    {
        var (_, _, task, _) = SetUpPendingRequest();

        var result = await CreateHandler().Handle(new RejectPrApprovalCommand(task.Id, "not good enough", Guid.NewGuid(), null, Roles.Engineer), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
        task.PrLink.Should().NotBeNull();
    }

    [Fact]
    public async Task Department_head_rejecting_clears_the_PR_link_and_notifies_the_requester()
    {
        var (_, head, task, requesterId) = SetUpPendingRequest();

        var result = await CreateHandler().Handle(new RejectPrApprovalCommand(task.Id, "needs more tests", head.Id, null, Roles.HeadOfRnD), default);

        result.IsSuccess.Should().BeTrue();
        task.PrLink.Should().BeNull();
        task.PendingPrApprovalRequestedAt.Should().BeNull();
        _notifications.Verify(n => n.AddAsync(It.Is<Domain.Notifications.Notification>(x => x.UserId == requesterId), default), Times.Once);
    }
}
