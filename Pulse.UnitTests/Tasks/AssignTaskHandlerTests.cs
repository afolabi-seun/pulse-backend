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

public class AssignTaskHandlerTests
{
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<IAuditLogRepository> _audit = new();
    private readonly Mock<IProjectRepository> _projects = new();
    private readonly Mock<IProjectAccessPolicy> _access = new();
    private readonly Mock<INotificationRepository> _notifications = new();
    private readonly Mock<IRealtimeNotifier> _realtime = new();
    private readonly Mock<IEmailQueue> _emailQueue = new();
    private readonly Mock<IAppSettings> _settings = new();

    public AssignTaskHandlerTests()
    {
        _settings.Setup(s => s.AppBaseUrl).Returns("https://pulse.test");
        _access
            .Setup(a => a.CanAccessTaskAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _teams.Setup(t => t.ListAllAsync(default)).ReturnsAsync(Array.Empty<Team>());
    }

    private AssignTaskHandler CreateHandler() =>
        new(_tasks.Object, _engineers.Object, _teams.Object, _audit.Object, _projects.Object, _access.Object,
            TestNotifications.Dispatcher(_notifications, _realtime, _emailQueue), _settings.Object);

    private static PulseTask UnassignedTask(bool withDueDate = true) =>
        PulseTask.Create("Unassigned work", 5, Guid.NewGuid(),
            dueDate: withDueDate ? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)) : null);

    [Fact]
    public async Task Returns_NOT_FOUND_when_task_missing()
    {
        _tasks.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((PulseTask?)null);

        var result = await CreateHandler().Handle(
            new AssignTaskCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, Roles.TeamLead), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task Rejects_a_task_that_already_has_an_assignee()
    {
        var task = UnassignedTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateHandler().Handle(
            new AssignTaskCommand(task.Id, Guid.NewGuid(), Guid.NewGuid(), null, Roles.TeamLead), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
    }

    [Fact]
    public async Task Returns_FORBIDDEN_when_actor_cannot_access_the_task()
    {
        var task = UnassignedTask();
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        _access
            .Setup(a => a.CanAccessTaskAsync(task.Id, It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await CreateHandler().Handle(
            new AssignTaskCommand(task.Id, Guid.NewGuid(), Guid.NewGuid(), null, Roles.TeamLead), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
    }

    [Fact]
    public async Task Rejects_assignment_when_the_task_has_no_due_date()
    {
        var task = UnassignedTask(withDueDate: false);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateHandler().Handle(
            new AssignTaskCommand(task.Id, Guid.NewGuid(), Guid.NewGuid(), null, Roles.TeamLead), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
        task.AssigneeId.Should().BeNull();
    }

    [Fact]
    public async Task TeamLead_cannot_assign_to_an_engineer_outside_their_own_team()
    {
        var actorId = Guid.NewGuid();
        var ledTeam = Team.Create("Platform", actorId, "Engineering");
        var otherTeam = Team.Create("Design", Guid.NewGuid(), "Design");
        _teams.Setup(t => t.ListAllAsync(default)).ReturnsAsync(new[] { ledTeam, otherTeam });

        var task = UnassignedTask();
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var outsider = Engineer.Create("Outside Engineer", "outside@test.io", "hash", Roles.Engineer, 20, 14);
        outsider.AssignToTeam(otherTeam.Id);
        _engineers.Setup(e => e.GetByIdAsync(outsider.Id, default)).ReturnsAsync(outsider);

        var result = await CreateHandler().Handle(
            new AssignTaskCommand(task.Id, outsider.Id, actorId, null, Roles.TeamLead), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
        task.AssigneeId.Should().BeNull();
    }

    [Fact]
    public async Task TeamLead_who_leads_no_team_cannot_assign_at_all()
    {
        var task = UnassignedTask();
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);
        var target = Engineer.Create("Target", "target@test.io", "hash", Roles.Engineer, 20, 14);
        _engineers.Setup(e => e.GetByIdAsync(target.Id, default)).ReturnsAsync(target);

        var result = await CreateHandler().Handle(
            new AssignTaskCommand(task.Id, target.Id, Guid.NewGuid(), null, Roles.TeamLead), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
    }

    [Fact]
    public async Task TeamLead_can_assign_to_an_engineer_on_their_own_team()
    {
        var actorId = Guid.NewGuid();
        var ledTeam = Team.Create("Platform", actorId, "Engineering");
        _teams.Setup(t => t.ListAllAsync(default)).ReturnsAsync(new[] { ledTeam });

        var task = UnassignedTask();
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var member = Engineer.Create("Team Member", "member@test.io", "hash", Roles.Engineer, 20, 14);
        member.AssignToTeam(ledTeam.Id);
        _engineers.Setup(e => e.GetByIdAsync(member.Id, default)).ReturnsAsync(member);

        var result = await CreateHandler().Handle(
            new AssignTaskCommand(task.Id, member.Id, actorId, null, Roles.TeamLead), default);

        result.IsSuccess.Should().BeTrue();
        task.AssigneeId.Should().Be(member.Id);
        _projects.Verify(p => p.AddMemberAsync(task.ProjectId, member.Id, default), Times.Once);
    }

    [Fact]
    public async Task ProjectManager_is_unrestricted_by_team()
    {
        var task = UnassignedTask();
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var anyEngineer = Engineer.Create("Any Engineer", "any@test.io", "hash", Roles.Engineer, 20, 14);
        _engineers.Setup(e => e.GetByIdAsync(anyEngineer.Id, default)).ReturnsAsync(anyEngineer);

        var result = await CreateHandler().Handle(
            new AssignTaskCommand(task.Id, anyEngineer.Id, Guid.NewGuid(), null, Roles.ProjectManager), default);

        result.IsSuccess.Should().BeTrue();
        task.AssigneeId.Should().Be(anyEngineer.Id);
    }

    [Fact]
    public async Task A_plain_Engineer_can_claim_an_unassigned_task_for_themselves()
    {
        var task = UnassignedTask();
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var self = Engineer.Create("Self Claimer", "self@test.io", "hash", Roles.Engineer, 20, 14);
        _engineers.Setup(e => e.GetByIdAsync(self.Id, default)).ReturnsAsync(self);

        var result = await CreateHandler().Handle(
            new AssignTaskCommand(task.Id, self.Id, self.Id, null, Roles.Engineer), default);

        result.IsSuccess.Should().BeTrue();
        task.AssigneeId.Should().Be(self.Id);
        _projects.Verify(p => p.AddMemberAsync(task.ProjectId, self.Id, default), Times.Once);
    }

    [Fact]
    public async Task A_plain_Engineer_cannot_assign_the_task_to_someone_else()
    {
        var task = UnassignedTask();
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var teammate = Engineer.Create("Teammate", "teammate@test.io", "hash", Roles.Engineer, 20, 14);
        _engineers.Setup(e => e.GetByIdAsync(teammate.Id, default)).ReturnsAsync(teammate);

        var result = await CreateHandler().Handle(
            new AssignTaskCommand(task.Id, teammate.Id, Guid.NewGuid(), null, Roles.Engineer), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
        task.AssigneeId.Should().BeNull();
    }
}
