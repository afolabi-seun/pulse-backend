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

public class RequestPrApprovalHandlerTests
{
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<IProjectAccessPolicy> _access = new();
    private readonly Mock<IAuditLogRepository> _audit = new();
    private readonly Mock<INotificationRepository> _notifications = new();
    private readonly Mock<IRealtimeNotifier> _realtime = new();
    private readonly Mock<IEmailQueue> _emailQueue = new();
    private readonly Mock<IAppSettings> _settings = new();

    public RequestPrApprovalHandlerTests()
    {
        _settings.Setup(s => s.AppBaseUrl).Returns("https://pulse.test");
        _teams.Setup(t => t.ListAllAsync(default)).ReturnsAsync([]);
        _engineers.Setup(e => e.ListActiveAsync(default)).ReturnsAsync([]);
    }

    private RequestPrApprovalHandler CreateHandler() => new(
        _tasks.Object, _engineers.Object, _teams.Object, _access.Object,
        _audit.Object, _settings.Object, TestNotifications.Dispatcher(_notifications, _realtime, _emailQueue));

    private (Team team, Engineer assignee, Engineer head, PulseTask task) SetUpTask()
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

        _tasks.Setup(t => t.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _teams.Setup(r => r.GetByIdAsync(team.Id, default)).ReturnsAsync(team);
        _teams.Setup(r => r.ListAllAsync(default)).ReturnsAsync([team]);
        _engineers.Setup(r => r.GetByIdAsync(assignee.Id, default)).ReturnsAsync(assignee);
        _engineers.Setup(r => r.GetByIdAsync(head.Id, default)).ReturnsAsync(head);
        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync([assignee, head]);

        return (team, assignee, head, task);
    }

    [Fact]
    public async Task Fails_with_NOT_FOUND_for_a_missing_task()
    {
        var taskId = Guid.NewGuid();
        _tasks.Setup(t => t.GetByIdAsync(taskId, default)).ReturnsAsync((PulseTask?)null);

        var result = await CreateHandler().Handle(new RequestPrApprovalCommand(taskId, "https://x/pr/1", Guid.NewGuid(), null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task Fails_with_FORBIDDEN_for_someone_who_is_neither_the_assignee_nor_has_project_access()
    {
        var (_, _, _, task) = SetUpTask();
        _access.Setup(a => a.CanAccessProjectAsync(task.ProjectId, It.IsAny<Guid>(), It.IsAny<string>(), default)).ReturnsAsync(false);

        var result = await CreateHandler().Handle(new RequestPrApprovalCommand(task.Id, "https://x/pr/1", Guid.NewGuid(), null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
    }

    [Fact]
    public async Task Assignee_can_request_approval_and_department_head_is_notified()
    {
        var (_, assignee, head, task) = SetUpTask();

        var result = await CreateHandler().Handle(new RequestPrApprovalCommand(task.Id, "https://x/pr/1", assignee.Id, null), default);

        result.IsSuccess.Should().BeTrue();
        task.PrLink.Should().Be("https://x/pr/1");
        task.PendingPrApprovalRequestedAt.Should().NotBeNull();
        _notifications.Verify(n => n.AddAsync(It.Is<Domain.Notifications.Notification>(x => x.UserId == head.Id), default), Times.Once);
    }

    [Fact]
    public async Task Fails_with_BUSINESS_RULE_VIOLATION_when_the_task_does_not_require_PR_approval()
    {
        var team = Team.Create("Platform");
        var assignee = Engineer.Create("Dev", "dev@x.io", "hash", Roles.Engineer, 20, 14);
        assignee.AssignToTeam(team.Id);
        var task = PulseTask.Create("Task", 3, Guid.NewGuid());
        task.Assign(assignee.Id, Guid.NewGuid());
        _tasks.Setup(t => t.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateHandler().Handle(new RequestPrApprovalCommand(task.Id, "https://x/pr/1", assignee.Id, null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
    }
}
