using Pulse.Application.Common.Interfaces;
using Pulse.Application.Overwork;
using Pulse.Domain.Engineers;
using Pulse.Domain.Overrides;
using Pulse.Domain.Tasks;
using Pulse.Domain.Teams;
using FluentAssertions;
using Moq;
using Pulse.UnitTests.Common;

namespace Pulse.UnitTests.Overwork;

public class OverworkDigestJobTests
{
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<IOverworkOverrideRepository> _overrides = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<IDepartmentThresholdRepository> _departmentThresholds = new();
    private readonly Mock<INotificationRepository> _notifications = new();
    private readonly Mock<IRealtimeNotifier> _realtime = new();
    private readonly Mock<IEmailQueue> _emailQueue = new();
    private readonly Mock<IAppSettings> _settings = new();
    private readonly OverworkSignalsCalculator _calculator = new(new OverworkThresholds());

    public OverworkDigestJobTests()
    {
        _settings.Setup(s => s.AppBaseUrl).Returns("https://pulse.test");
        _overrides.Setup(r => r.GetAllActiveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<OverworkOverride>());
        _teams.Setup(r => r.ListAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<Team>());
        _engineers.Setup(r => r.ListByRoleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<Engineer>());
        _departmentThresholds.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<DepartmentThresholdOverride>());
    }

    private OverworkDigestJob CreateJob() =>
        new(_engineers.Object, _overrides.Object, _calculator, _teams.Object, _departmentThresholds.Object, TestNotifications.Dispatcher(_notifications, _realtime, _emailQueue), _settings.Object);

    private static Engineer NewEngineer(string name, int baselinePoints = 20) =>
        Engineer.Create(name, $"{name.Replace(" ", ".").ToLower()}@pulse.io", "pw", Roles.Engineer, baselinePoints, 14);

    // Due today, not 30 days out — the load signal now only counts points due within the
    // engineer's baseline cycle, so a task meant to register as overload has to actually fall
    // inside that window.
    private static PulseTask OverloadTask(int points) =>
        PulseTask.Create("Task", points, Guid.NewGuid(), dueDate: DateOnly.FromDateTime(DateTime.UtcNow));

    [Fact]
    public async Task Sends_nothing_when_no_one_is_overworked()
    {
        var engineer = NewEngineer("Not Overloaded");
        _engineers.Setup(r => r.ListActiveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { engineer });
        _engineers.Setup(r => r.GetWithActiveTasksAsync(engineer.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((engineer, (IReadOnlyList<PulseTask>)Array.Empty<PulseTask>()));

        await CreateJob().RunAsync();

        _emailQueue.Verify(q => q.Enqueue(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Notifies_team_lead_for_their_own_overworked_member()
    {
        var team = Team.Create("Team A", null);
        var lead = NewEngineer("Lead One");
        lead.AssignToTeam(team.Id);
        team.SetTeamLead(lead.Id);
        var overworked = NewEngineer("Overloaded Dev", baselinePoints: 10);
        overworked.AssignToTeam(team.Id);

        _teams.Setup(r => r.ListAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { team });
        _engineers.Setup(r => r.ListActiveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { overworked });
        _engineers.Setup(r => r.GetWithActiveTasksAsync(overworked.Id, It.IsAny<CancellationToken>()))
            // 4 tasks (> MaxConcurrentTasks=3) at 5pts each (20 > baseline*1.3=13) — trips both
            // the load and concurrent signals, since SignalsRequiredToFlag defaults to 2.
            .ReturnsAsync((overworked, (IReadOnlyList<PulseTask>)[OverloadTask(5), OverloadTask(5), OverloadTask(5), OverloadTask(5)]));
        _engineers.Setup(r => r.GetByIdAsync(lead.Id, It.IsAny<CancellationToken>())).ReturnsAsync(lead);

        await CreateJob().RunAsync();

        _emailQueue.Verify(q => q.Enqueue(lead.Email, It.Is<string>(s => s.Contains("1 engineer")), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Skips_engineers_with_an_active_override()
    {
        var overworked = NewEngineer("Overridden Dev", baselinePoints: 10);
        _overrides.Setup(r => r.GetAllActiveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { OverworkOverride.Grant(overworked.Id, "planned crunch", DateTime.UtcNow.AddDays(7), Guid.NewGuid()) });
        _engineers.Setup(r => r.ListActiveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { overworked });

        await CreateJob().RunAsync();

        _engineers.Verify(r => r.GetWithActiveTasksAsync(overworked.Id, It.IsAny<CancellationToken>()), Times.Never);
        _emailQueue.Verify(q => q.Enqueue(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Notifies_PMO_with_a_rolled_up_count_not_a_per_engineer_list()
    {
        var overworked = NewEngineer("Overloaded Dev", baselinePoints: 10);
        var pmo = NewEngineer("PMO Head");
        _engineers.Setup(r => r.ListActiveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { overworked });
        _engineers.Setup(r => r.GetWithActiveTasksAsync(overworked.Id, It.IsAny<CancellationToken>()))
            // 4 tasks (> MaxConcurrentTasks=3) at 5pts each (20 > baseline*1.3=13) — trips both
            // the load and concurrent signals, since SignalsRequiredToFlag defaults to 2.
            .ReturnsAsync((overworked, (IReadOnlyList<PulseTask>)[OverloadTask(5), OverloadTask(5), OverloadTask(5), OverloadTask(5)]));
        _engineers.Setup(r => r.ListByRoleAsync(Roles.HeadOfPmo, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { pmo });

        await CreateJob().RunAsync();

        _emailQueue.Verify(q => q.Enqueue(
            pmo.Email,
            It.Is<string>(s => s.Contains("1 engineer")),
            It.Is<string>(body => !body.Contains(overworked.Name))), Times.Once);
    }
}
