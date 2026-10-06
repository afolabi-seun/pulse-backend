using Pulse.Application.Common.Interfaces;
using Pulse.Application.Tasks.Commands;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using Pulse.Domain.Teams;
using FluentAssertions;
using Moq;
using Pulse.UnitTests.Common;

namespace Pulse.UnitTests.Tasks;

public class LoanTaskHandlerTests
{
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<IAuditLogRepository> _audit = new();
    private readonly Mock<IEscalationEventRepository> _escalationEvents = new();
    private readonly Mock<IProjectRepository> _projects = new();
    private readonly Mock<INotificationRepository> _notifications = new();
    private readonly Mock<IRealtimeNotifier> _realtime = new();
    private readonly Mock<IEmailQueue> _emailQueue = new();
    private readonly Mock<IAppSettings> _settings = new();

    public LoanTaskHandlerTests()
    {
        _settings.Setup(s => s.AppBaseUrl).Returns("https://pulse.test");
    }

    private LoanTaskHandler CreateHandler() =>
        new(_tasks.Object, _engineers.Object, _teams.Object, _audit.Object, _escalationEvents.Object, _projects.Object,
            TestNotifications.Dispatcher(_notifications, _realtime, _emailQueue), _settings.Object);

    [Fact]
    public async Task Rejects_loaning_a_QA_task_to_a_non_QA_engineer()
    {
        var actorId = Guid.NewGuid();
        var actorTeam = Team.Create("Platform", actorId, "Engineering");
        var currentAssignee = Engineer.Create("Reviewer's own dev", "dev@test.io", "hash", Roles.Engineer, 20, 14);
        currentAssignee.AssignToTeam(actorTeam.Id);
        _teams.Setup(t => t.ListAllAsync(default)).ReturnsAsync(new[] { actorTeam });

        var qaTask = PulseTask.Create("[QA] Ship the thing", 5, Guid.NewGuid(),
            dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)));
        qaTask.SetParentTaskId(Guid.NewGuid());
        qaTask.Assign(currentAssignee.Id, actorId);
        _tasks.Setup(r => r.GetByIdAsync(qaTask.Id, default)).ReturnsAsync(qaTask);
        _engineers.Setup(e => e.GetByIdAsync(currentAssignee.Id, default)).ReturnsAsync(currentAssignee);

        var nonQaTarget = Engineer.Create("Not QA", "notqa@test.io", "hash", Roles.Engineer, 20, 14);
        _engineers.Setup(e => e.GetByIdAsync(nonQaTarget.Id, default)).ReturnsAsync(nonQaTarget);

        var result = await CreateHandler().Handle(
            new LoanTaskCommand(qaTask.Id, nonQaTarget.Id, null, actorId, null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
        qaTask.AssigneeId.Should().Be(currentAssignee.Id, "the rejected loan must not have applied");
    }

    [Fact]
    public async Task Rejects_loaning_a_no_discipline_QA_task_when_actor_is_not_an_allowed_head()
    {
        var actorId = Guid.NewGuid();
        var actorTeam = Team.Create("Platform", actorId, "Engineering");
        var currentAssignee = Engineer.Create("Reviewer's own dev", "dev@test.io", "hash", Roles.Engineer, 20, 14);
        currentAssignee.AssignToTeam(actorTeam.Id);
        _teams.Setup(t => t.ListAllAsync(default)).ReturnsAsync(new[] { actorTeam });

        var parent = PulseTask.Create("Ship the thing", 5, Guid.NewGuid()); // no discipline set
        var qaTask = PulseTask.Create("[QA] Ship the thing", 5, parent.ProjectId,
            dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)));
        qaTask.SetParentTaskId(parent.Id);
        qaTask.Assign(currentAssignee.Id, actorId);
        _tasks.Setup(r => r.GetByIdAsync(qaTask.Id, default)).ReturnsAsync(qaTask);
        _tasks.Setup(r => r.GetByIdAsync(parent.Id, default)).ReturnsAsync(parent);
        _engineers.Setup(e => e.GetByIdAsync(currentAssignee.Id, default)).ReturnsAsync(currentAssignee);

        var qaTarget = Engineer.Create("QA Reviewer", "qa@test.io", "hash", Roles.Engineer, 20, 14);
        qaTarget.SetIsQa(true);
        _engineers.Setup(e => e.GetByIdAsync(qaTarget.Id, default)).ReturnsAsync(qaTarget);

        // Actor leads a team (satisfies the loan-eligibility check) but isn't Head of Product/PMO/Functional.
        var result = await CreateHandler().Handle(
            new LoanTaskCommand(qaTask.Id, qaTarget.Id, null, actorId, null, Roles.TeamLead), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
        qaTask.AssigneeId.Should().Be(currentAssignee.Id, "the rejected loan must not have applied");
    }
}
