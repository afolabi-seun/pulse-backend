using Pulse.Application.Automations;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Automations;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using Pulse.Domain.Teams;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Automations;

public class AutomationRuleScannerTests
{
    private readonly Mock<IAutomationRuleRepository> _rules = new();
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<IAutomationExecutionRepository> _executions = new();
    private readonly Mock<IAuditLogRepository> _audit = new();
    private readonly Mock<INotificationRepository> _notifications = new();
    private readonly Mock<IRealtimeNotifier> _realtime = new();
    private readonly Mock<IEmailQueue> _emailQueue = new();
    private readonly Mock<IAppSettings> _settings = new();

    public AutomationRuleScannerTests()
    {
        _settings.Setup(s => s.AppBaseUrl).Returns("https://pulse.test");
        // Loose mock default — no prior execution for any (rule, task) pair unless a test says otherwise.
        _executions
            .Setup(e => e.GetLatestAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), default))
            .ReturnsAsync((AutomationExecution?)null);
    }

    private AutomationRuleScanner CreateScanner() =>
        new(_rules.Object, _tasks.Object, _engineers.Object, _teams.Object, _executions.Object,
            _audit.Object, _notifications.Object, _realtime.Object, _emailQueue.Object, _settings.Object);

    private static void SetActivatedAt(PulseTask task, DateTime value)
    {
        var prop = typeof(PulseTask).GetProperty("ActivatedAt")!;
        prop.SetValue(task, value);
    }

    private static PulseTask BlockedTask(Guid projectId, Guid assigneeId, DateTime activatedAt)
    {
        var task = PulseTask.Create("Stuck task", 3, projectId);
        task.Assign(assigneeId, assigneeId);
        task.FlagBlocker("stuck", assigneeId);
        SetActivatedAt(task, activatedAt);
        return task;
    }

    [Fact]
    public async Task Does_nothing_when_there_are_no_active_rules()
    {
        _rules.Setup(r => r.ListActiveAsync(default)).ReturnsAsync(Array.Empty<AutomationRule>());

        await CreateScanner().RunAsync();

        _tasks.Verify(t => t.GetBlockedTasksAsync(default), Times.Never);
    }

    [Fact]
    public async Task Does_nothing_when_the_team_has_no_lead()
    {
        var rule = AutomationRule.Create(Guid.NewGuid(), "Rule", Guid.NewGuid(), 5);
        _rules.Setup(r => r.ListActiveAsync(default)).ReturnsAsync(new[] { rule });
        _teams.Setup(t => t.GetByIdAsync(rule.TeamId, default)).ReturnsAsync(Team.Create("Team", null, "Engineering"));
        _tasks.Setup(t => t.GetBlockedTasksAsync(default)).ReturnsAsync(Array.Empty<PulseTask>());

        await CreateScanner().RunAsync();

        _tasks.Verify(t => t.SaveChangesAsync(default), Times.Never);
    }

    [Fact]
    public async Task Does_nothing_when_the_lead_is_inactive()
    {
        var lead = Engineer.Create("Lead", "lead@test.io", "hash", Roles.TeamLead, 20, 14);
        lead.Deactivate();
        var rule = AutomationRule.Create(Guid.NewGuid(), "Rule", Guid.NewGuid(), 5);
        _rules.Setup(r => r.ListActiveAsync(default)).ReturnsAsync(new[] { rule });
        _teams.Setup(t => t.GetByIdAsync(rule.TeamId, default)).ReturnsAsync(Team.Create("Team", lead.Id, "Engineering"));
        _engineers.Setup(e => e.GetByIdAsync(lead.Id, default)).ReturnsAsync(lead);
        _tasks.Setup(t => t.GetBlockedTasksAsync(default)).ReturnsAsync(Array.Empty<PulseTask>());

        await CreateScanner().RunAsync();

        _tasks.Verify(t => t.SaveChangesAsync(default), Times.Never);
    }

    [Fact]
    public async Task Reassigns_a_task_blocked_past_the_threshold_to_the_team_lead()
    {
        var lead = Engineer.Create("Lead", "lead2@test.io", "hash", Roles.TeamLead, 20, 14);
        var engineer = Engineer.Create("Engineer", "eng2@test.io", "hash", Roles.Engineer, 20, 14);
        var teamId = Guid.NewGuid();
        lead.AssignToTeam(teamId);
        engineer.AssignToTeam(teamId);
        var rule = AutomationRule.Create(Guid.NewGuid(), "Rule", teamId, 5);

        var task = BlockedTask(Guid.NewGuid(), engineer.Id, DateTime.UtcNow.AddDays(-6));

        _rules.Setup(r => r.ListActiveAsync(default)).ReturnsAsync(new[] { rule });
        _teams.Setup(t => t.GetByIdAsync(teamId, default)).ReturnsAsync(Team.Create("Team", lead.Id, "Engineering"));
        _engineers.Setup(e => e.GetByIdAsync(lead.Id, default)).ReturnsAsync(lead);
        _engineers.Setup(e => e.GetByIdAsync(engineer.Id, default)).ReturnsAsync(engineer);
        _engineers.Setup(e => e.ListAllAsync(default)).ReturnsAsync(new[] { lead, engineer });
        _tasks.Setup(t => t.GetBlockedTasksAsync(default)).ReturnsAsync(new[] { task });

        await CreateScanner().RunAsync();

        task.AssigneeId.Should().Be(lead.Id);
        _tasks.Verify(t => t.SaveChangesAsync(default), Times.Once);
        _executions.Verify(e => e.AddAsync(
            It.Is<AutomationExecution>(x => x.AutomationRuleId == rule.Id && x.TaskId == task.Id), default), Times.Once);
        _audit.Verify(a => a.LogAsync("AUTOMATION_TASK_REASSIGNED", rule.OwnerEngineerId, null, It.IsAny<string>(), default), Times.Once);
        _emailQueue.Verify(e => e.Enqueue(lead.Email, It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        _notifications.Verify(n => n.AddAsync(It.IsAny<Domain.Notifications.Notification>(), default), Times.Exactly(2)); // lead + previous assignee
    }

    [Fact]
    public async Task Does_not_reassign_a_task_blocked_for_fewer_days_than_the_threshold()
    {
        var lead = Engineer.Create("Lead", "lead3@test.io", "hash", Roles.TeamLead, 20, 14);
        var engineer = Engineer.Create("Engineer", "eng3@test.io", "hash", Roles.Engineer, 20, 14);
        var teamId = Guid.NewGuid();
        lead.AssignToTeam(teamId);
        engineer.AssignToTeam(teamId);
        var rule = AutomationRule.Create(Guid.NewGuid(), "Rule", teamId, 5);

        var task = BlockedTask(Guid.NewGuid(), engineer.Id, DateTime.UtcNow.AddDays(-2));

        _rules.Setup(r => r.ListActiveAsync(default)).ReturnsAsync(new[] { rule });
        _teams.Setup(t => t.GetByIdAsync(teamId, default)).ReturnsAsync(Team.Create("Team", lead.Id, "Engineering"));
        _engineers.Setup(e => e.GetByIdAsync(lead.Id, default)).ReturnsAsync(lead);
        _engineers.Setup(e => e.ListAllAsync(default)).ReturnsAsync(new[] { lead, engineer });
        _tasks.Setup(t => t.GetBlockedTasksAsync(default)).ReturnsAsync(new[] { task });

        await CreateScanner().RunAsync();

        task.AssigneeId.Should().Be(engineer.Id);
        _tasks.Verify(t => t.SaveChangesAsync(default), Times.Never);
    }

    [Fact]
    public async Task Does_not_reassign_a_task_already_assigned_to_the_lead()
    {
        var lead = Engineer.Create("Lead", "lead4@test.io", "hash", Roles.TeamLead, 20, 14);
        var teamId = Guid.NewGuid();
        lead.AssignToTeam(teamId);
        var rule = AutomationRule.Create(Guid.NewGuid(), "Rule", teamId, 5);

        var task = BlockedTask(Guid.NewGuid(), lead.Id, DateTime.UtcNow.AddDays(-10));

        _rules.Setup(r => r.ListActiveAsync(default)).ReturnsAsync(new[] { rule });
        _teams.Setup(t => t.GetByIdAsync(teamId, default)).ReturnsAsync(Team.Create("Team", lead.Id, "Engineering"));
        _engineers.Setup(e => e.GetByIdAsync(lead.Id, default)).ReturnsAsync(lead);
        _engineers.Setup(e => e.ListAllAsync(default)).ReturnsAsync(new[] { lead });
        _tasks.Setup(t => t.GetBlockedTasksAsync(default)).ReturnsAsync(new[] { task });

        await CreateScanner().RunAsync();

        _tasks.Verify(t => t.SaveChangesAsync(default), Times.Never);
    }

    [Fact]
    public async Task Does_not_reassign_a_task_whose_current_assignee_is_on_a_different_team()
    {
        var lead = Engineer.Create("Lead", "lead5@test.io", "hash", Roles.TeamLead, 20, 14);
        var outsider = Engineer.Create("Outsider", "outsider5@test.io", "hash", Roles.Engineer, 20, 14);
        var teamId = Guid.NewGuid();
        lead.AssignToTeam(teamId);
        // outsider is NOT assigned to teamId — a different team's engineer
        var rule = AutomationRule.Create(Guid.NewGuid(), "Rule", teamId, 5);

        var task = BlockedTask(Guid.NewGuid(), outsider.Id, DateTime.UtcNow.AddDays(-10));

        _rules.Setup(r => r.ListActiveAsync(default)).ReturnsAsync(new[] { rule });
        _teams.Setup(t => t.GetByIdAsync(teamId, default)).ReturnsAsync(Team.Create("Team", lead.Id, "Engineering"));
        _engineers.Setup(e => e.GetByIdAsync(lead.Id, default)).ReturnsAsync(lead);
        _engineers.Setup(e => e.ListAllAsync(default)).ReturnsAsync(new[] { lead, outsider });
        _tasks.Setup(t => t.GetBlockedTasksAsync(default)).ReturnsAsync(new[] { task });

        await CreateScanner().RunAsync();

        _tasks.Verify(t => t.SaveChangesAsync(default), Times.Never);
    }

    [Fact]
    public async Task Does_not_re_reassign_a_task_already_acted_on_during_this_same_blocked_stretch()
    {
        var lead = Engineer.Create("Lead", "lead6@test.io", "hash", Roles.TeamLead, 20, 14);
        var engineer = Engineer.Create("Engineer", "eng6@test.io", "hash", Roles.Engineer, 20, 14);
        var teamId = Guid.NewGuid();
        lead.AssignToTeam(teamId);
        engineer.AssignToTeam(teamId);
        var rule = AutomationRule.Create(Guid.NewGuid(), "Rule", teamId, 5);

        var activatedAt = DateTime.UtcNow.AddDays(-10);
        var task = BlockedTask(Guid.NewGuid(), engineer.Id, activatedAt);
        var priorExecution = AutomationExecution.Create(rule.Id, task.Id); // CreatedAt = now, after activatedAt

        _rules.Setup(r => r.ListActiveAsync(default)).ReturnsAsync(new[] { rule });
        _teams.Setup(t => t.GetByIdAsync(teamId, default)).ReturnsAsync(Team.Create("Team", lead.Id, "Engineering"));
        _engineers.Setup(e => e.GetByIdAsync(lead.Id, default)).ReturnsAsync(lead);
        _engineers.Setup(e => e.ListAllAsync(default)).ReturnsAsync(new[] { lead, engineer });
        _tasks.Setup(t => t.GetBlockedTasksAsync(default)).ReturnsAsync(new[] { task });
        _executions.Setup(e => e.GetLatestAsync(rule.Id, task.Id, default)).ReturnsAsync(priorExecution);

        await CreateScanner().RunAsync();

        task.AssigneeId.Should().Be(engineer.Id);
        _tasks.Verify(t => t.SaveChangesAsync(default), Times.Never);
    }
}
