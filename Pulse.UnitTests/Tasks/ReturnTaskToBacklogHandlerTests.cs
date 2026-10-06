using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Tasks.Commands;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using Pulse.Domain.Teams;
using FluentAssertions;
using Moq;
using Pulse.UnitTests.Common;

namespace Pulse.UnitTests.Tasks;

public class ReturnTaskToBacklogHandlerTests
{
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<IAuditLogRepository> _audit = new();
    private readonly Mock<IProjectAccessPolicy> _access = new();
    private readonly Mock<INotificationRepository> _notifications = new();
    private readonly Mock<IRealtimeNotifier> _realtime = new();

    public ReturnTaskToBacklogHandlerTests()
    {
        _access
            .Setup(a => a.CanAccessTaskAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _teams.Setup(t => t.ListAllAsync(default)).ReturnsAsync(Array.Empty<Team>());
    }

    private ReturnTaskToBacklogHandler CreateHandler() =>
        new(_tasks.Object, _engineers.Object, _teams.Object, _audit.Object, _access.Object,
            TestNotifications.Dispatcher(_notifications, _realtime));

    private static PulseTask ActiveTask(Guid assigneeId)
    {
        var task = PulseTask.Create("Stuck task", 5, Guid.NewGuid(), dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)));
        task.Assign(assigneeId, assigneeId);
        return task;
    }

    [Fact]
    public async Task Returns_NOT_FOUND_when_task_missing()
    {
        _tasks.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((PulseTask?)null);

        var result = await CreateHandler().Handle(
            new ReturnTaskToBacklogCommand(Guid.NewGuid(), Guid.NewGuid(), null, Roles.ProjectManager), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task Returns_FORBIDDEN_when_actor_cannot_access_the_task()
    {
        var task = ActiveTask(Guid.NewGuid());
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _access
            .Setup(a => a.CanAccessTaskAsync(task.Id, It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await CreateHandler().Handle(
            new ReturnTaskToBacklogCommand(task.Id, Guid.NewGuid(), null, Roles.ProjectManager), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
    }

    [Fact]
    public async Task Returns_BUSINESS_RULE_VIOLATION_when_the_task_is_not_Active()
    {
        var task = PulseTask.Create("Ungroomed", 3, Guid.NewGuid()); // Backlog, never assigned
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateHandler().Handle(
            new ReturnTaskToBacklogCommand(task.Id, Guid.NewGuid(), null, Roles.ProjectManager), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
    }

    [Fact]
    public async Task TeamLead_cannot_return_a_task_held_by_an_engineer_outside_their_own_team()
    {
        var actorId = Guid.NewGuid();
        var ledTeam = Team.Create("Platform", actorId, "Engineering");
        var otherTeam = Team.Create("Design", Guid.NewGuid(), "Design");
        _teams.Setup(t => t.ListAllAsync(default)).ReturnsAsync(new[] { ledTeam, otherTeam });

        var outsider = Engineer.Create("Outsider", "outsider_rtb@test.io", "hash", Roles.Engineer, 20, 14);
        outsider.AssignToTeam(otherTeam.Id);
        var task = ActiveTask(outsider.Id);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _engineers.Setup(e => e.GetByIdAsync(outsider.Id, default)).ReturnsAsync(outsider);

        var result = await CreateHandler().Handle(
            new ReturnTaskToBacklogCommand(task.Id, actorId, null, Roles.TeamLead), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
        task.Status.Should().Be(Pulse.Domain.Tasks.TaskStatus.Active);
    }

    [Fact]
    public async Task TeamLead_who_leads_no_team_cannot_return_a_task_at_all()
    {
        var member = Engineer.Create("Member", "member_rtb@test.io", "hash", Roles.Engineer, 20, 14);
        var task = ActiveTask(member.Id);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _engineers.Setup(e => e.GetByIdAsync(member.Id, default)).ReturnsAsync(member);

        var result = await CreateHandler().Handle(
            new ReturnTaskToBacklogCommand(task.Id, Guid.NewGuid(), null, Roles.TeamLead), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
    }

    [Fact]
    public async Task TeamLead_can_return_a_task_held_by_an_engineer_on_their_own_team()
    {
        var actorId = Guid.NewGuid();
        var ledTeam = Team.Create("Platform", actorId, "Engineering");
        _teams.Setup(t => t.ListAllAsync(default)).ReturnsAsync(new[] { ledTeam });

        var member = Engineer.Create("Member", "member_rtb2@test.io", "hash", Roles.Engineer, 20, 14);
        member.AssignToTeam(ledTeam.Id);
        var task = ActiveTask(member.Id);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _engineers.Setup(e => e.GetByIdAsync(member.Id, default)).ReturnsAsync(member);

        var result = await CreateHandler().Handle(
            new ReturnTaskToBacklogCommand(task.Id, actorId, null, Roles.TeamLead), default);

        result.IsSuccess.Should().BeTrue();
        task.Status.Should().Be(Pulse.Domain.Tasks.TaskStatus.Backlog);
        task.AssigneeId.Should().BeNull();
        _audit.Verify(a => a.LogAsync("TASK_RETURNED_TO_BACKLOG", actorId, null, It.IsAny<string>(), default), Times.Once);
        _notifications.Verify(n => n.AddAsync(It.IsAny<Pulse.Domain.Notifications.Notification>(), default), Times.Once);
    }

    [Fact]
    public async Task ProjectManager_is_unrestricted_by_team()
    {
        var member = Engineer.Create("Any Engineer", "any_rtb@test.io", "hash", Roles.Engineer, 20, 14);
        var task = ActiveTask(member.Id);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateHandler().Handle(
            new ReturnTaskToBacklogCommand(task.Id, Guid.NewGuid(), null, Roles.ProjectManager), default);

        result.IsSuccess.Should().BeTrue();
        task.Status.Should().Be(Pulse.Domain.Tasks.TaskStatus.Backlog);
    }
}
