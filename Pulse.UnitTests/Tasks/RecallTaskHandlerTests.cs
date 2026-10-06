using Pulse.Application.Common.Interfaces;
using Pulse.Application.Tasks.Commands;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using Pulse.Domain.Teams;
using FluentAssertions;
using Moq;
using Pulse.UnitTests.Common;

namespace Pulse.UnitTests.Tasks;

public class RecallTaskHandlerTests
{
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<IAuditLogRepository> _audit = new();
    private readonly Mock<IEscalationEventRepository> _escalationEvents = new();
    private readonly Mock<INotificationRepository> _notifications = new();
    private readonly Mock<IRealtimeNotifier> _realtime = new();
    private readonly Mock<IEmailQueue> _emailQueue = new();
    private readonly Mock<IAppSettings> _settings = new();

    public RecallTaskHandlerTests()
    {
        _settings.Setup(s => s.AppBaseUrl).Returns("https://pulse.test");
    }

    private RecallTaskHandler CreateHandler() =>
        new(_tasks.Object, _engineers.Object, _teams.Object, _audit.Object, _escalationEvents.Object,
            TestNotifications.Dispatcher(_notifications, _realtime, _emailQueue), _settings.Object);

    [Fact]
    public async Task Rejects_recalling_a_task_that_was_never_loaned()
    {
        var actorId = Guid.NewGuid();
        var task = PulseTask.Create("Never loaned", 5, Guid.NewGuid(),
            dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)));
        task.Assign(Guid.NewGuid(), actorId);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateHandler().Handle(new RecallTaskCommand(task.Id, actorId, null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
    }

    [Fact]
    public async Task Rejects_recall_by_a_team_lead_who_did_not_lend_the_task()
    {
        var actorId = Guid.NewGuid();
        var actorTeam = Team.Create("Some other team", actorId, "Engineering");
        _teams.Setup(t => t.ListAllAsync(default)).ReturnsAsync(new[] { actorTeam });

        var originalTeam = Team.Create("Lending team", Guid.NewGuid(), "Engineering");
        var original = Engineer.Create("Original owner", "orig@test.io", "hash", Roles.Engineer, 20, 14);
        original.AssignToTeam(originalTeam.Id);
        _engineers.Setup(e => e.GetByIdAsync(original.Id, default)).ReturnsAsync(original);

        var task = PulseTask.Create("On loan", 5, Guid.NewGuid(),
            dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)));
        task.Assign(original.Id, actorId);
        task.Loan(Guid.NewGuid(), actorId);
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateHandler().Handle(
            new RecallTaskCommand(task.Id, actorId, null, Roles.TeamLead), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
        task.AssigneeId.Should().NotBe(original.Id, "the rejected recall must not have applied");
    }

    [Fact]
    public async Task Allows_a_department_head_to_recall_a_task_they_did_not_personally_lend()
    {
        var actorId = Guid.NewGuid();
        var original = Engineer.Create("Original owner", "orig2@test.io", "hash", Roles.Engineer, 20, 14);
        _engineers.Setup(e => e.GetByIdAsync(original.Id, default)).ReturnsAsync(original);
        var borrower = Guid.NewGuid();

        var task = PulseTask.Create("On loan to another dept", 5, Guid.NewGuid(),
            dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)));
        task.Assign(original.Id, Guid.NewGuid());
        task.Loan(borrower, Guid.NewGuid());
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        var result = await CreateHandler().Handle(
            new RecallTaskCommand(task.Id, actorId, null, Roles.HeadOfPmo), default);

        result.IsSuccess.Should().BeTrue();
        task.AssigneeId.Should().Be(original.Id);
        task.LoanedFromEngineerId.Should().BeNull();
    }

    [Fact]
    public async Task Allows_the_current_holder_to_recall_their_own_loaned_task_without_lead_or_head_privileges()
    {
        var original = Engineer.Create("Original owner", "orig3@test.io", "hash", Roles.Engineer, 20, 14);
        _engineers.Setup(e => e.GetByIdAsync(original.Id, default)).ReturnsAsync(original);
        var borrowerId = Guid.NewGuid();

        var task = PulseTask.Create("On loan to borrower", 5, Guid.NewGuid(),
            dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)));
        task.Assign(original.Id, Guid.NewGuid());
        task.Loan(borrowerId, Guid.NewGuid());
        _tasks.Setup(r => r.GetByIdAsync(task.Id, default)).ReturnsAsync(task);

        // Borrower recalls as themselves, as a plain Engineer — no team-lead/head role involved.
        var result = await CreateHandler().Handle(
            new RecallTaskCommand(task.Id, borrowerId, null, Roles.Engineer), default);

        result.IsSuccess.Should().BeTrue();
        task.AssigneeId.Should().Be(original.Id);
        task.LoanedFromEngineerId.Should().BeNull();
    }
}
