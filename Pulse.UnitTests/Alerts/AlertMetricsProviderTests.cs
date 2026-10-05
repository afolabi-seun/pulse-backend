using Pulse.Application.Alerts;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Projects;
using Pulse.Domain.Alerts;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Alerts;

public class AlertMetricsProviderTests
{
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ICheckInRepository> _checkIns = new();
    private readonly Mock<IProjectRepository> _projects = new();
    private readonly Mock<IAlertMetricSnapshotRepository> _snapshots = new();

    public AlertMetricsProviderTests()
    {
        // Loose mock default — no snapshot history recorded yet, so every "Change" metric falls
        // back to its "not enough history" zero, matching TeamVelocityChange's own edge case.
        _snapshots
            .Setup(s => s.GetMostRecentBeforeAsync(It.IsAny<AlertMetric>(), It.IsAny<AlertScopeType>(), It.IsAny<Guid>(), It.IsAny<DateTime>(), default))
            .ReturnsAsync((AlertMetricSnapshot?)null);
    }

    private AlertMetricsProvider CreateProvider() =>
        new(_tasks.Object, _engineers.Object, _checkIns.Object, _projects.Object, _snapshots.Object);

    private static PulseTask BlockedTask(Guid projectId, Guid? assigneeId)
    {
        var task = PulseTask.Create("Blocked", 3, projectId);
        if (assigneeId.HasValue)
            task.Assign(assigneeId.Value, assigneeId.Value);
        task.FlagBlocker("stuck", Guid.NewGuid());
        return task;
    }

    [Fact]
    public async Task BlockerCount_scoped_to_Project_only_counts_that_projects_blockers()
    {
        var projectA = Guid.NewGuid();
        var projectB = Guid.NewGuid();
        _tasks.Setup(t => t.GetBlockedTasksAsync(default)).ReturnsAsync(new[]
        {
            BlockedTask(projectA, Guid.NewGuid()),
            BlockedTask(projectA, Guid.NewGuid()),
            BlockedTask(projectB, Guid.NewGuid()),
        });

        var value = await CreateProvider().GetCurrentValueAsync(AlertMetric.BlockerCount, AlertScopeType.Project, projectA, default);

        value.Should().Be(2);
    }

    [Fact]
    public async Task BlockerCount_scoped_to_Team_only_counts_blockers_assigned_to_that_teams_roster()
    {
        var teamId = Guid.NewGuid();
        var onTeam = Engineer.Create("On team", "on@test.io", "hash", Roles.Engineer, 20, 14);
        onTeam.AssignToTeam(teamId);
        var offTeam = Engineer.Create("Off team", "off@test.io", "hash", Roles.Engineer, 20, 14);

        _engineers.Setup(e => e.ListAllAsync(default)).ReturnsAsync(new[] { onTeam, offTeam });
        _tasks.Setup(t => t.GetBlockedTasksAsync(default)).ReturnsAsync(new[]
        {
            BlockedTask(Guid.NewGuid(), onTeam.Id),
            BlockedTask(Guid.NewGuid(), offTeam.Id),
        });

        var value = await CreateProvider().GetCurrentValueAsync(AlertMetric.BlockerCount, AlertScopeType.Team, teamId, default);

        value.Should().Be(1);
    }

    [Fact]
    public async Task TeamVelocity_returns_the_most_recent_weeks_delivered_points()
    {
        var teamId = Guid.NewGuid();
        _tasks.Setup(t => t.GetWeeklyThroughputByTeamAsync(teamId, default)).ReturnsAsync(new[]
        {
            new WeeklyThroughputPoint(new DateOnly(2026, 9, 1), 10),
            new WeeklyThroughputPoint(new DateOnly(2026, 9, 15), 30),
            new WeeklyThroughputPoint(new DateOnly(2026, 9, 8), 20),
        });

        var value = await CreateProvider().GetCurrentValueAsync(AlertMetric.TeamVelocity, AlertScopeType.Team, teamId, default);

        value.Should().Be(30);
    }

    [Fact]
    public async Task TeamVelocityChange_computes_the_percent_change_versus_the_prior_week()
    {
        var teamId = Guid.NewGuid();
        _tasks.Setup(t => t.GetWeeklyThroughputByTeamAsync(teamId, default)).ReturnsAsync(new[]
        {
            new WeeklyThroughputPoint(new DateOnly(2026, 9, 8), 20),  // prior week
            new WeeklyThroughputPoint(new DateOnly(2026, 9, 15), 12), // latest week — a 40% drop
        });

        var value = await CreateProvider().GetCurrentValueAsync(AlertMetric.TeamVelocityChange, AlertScopeType.Team, teamId, default);

        value.Should().Be(-40);
    }

    [Fact]
    public async Task TeamVelocityChange_is_zero_when_there_is_only_one_weeks_history()
    {
        var teamId = Guid.NewGuid();
        _tasks.Setup(t => t.GetWeeklyThroughputByTeamAsync(teamId, default)).ReturnsAsync(new[]
        {
            new WeeklyThroughputPoint(new DateOnly(2026, 9, 15), 12),
        });

        var value = await CreateProvider().GetCurrentValueAsync(AlertMetric.TeamVelocityChange, AlertScopeType.Team, teamId, default);

        value.Should().Be(0);
    }

    [Fact]
    public async Task TeamVelocityChange_is_zero_when_the_prior_week_delivered_nothing()
    {
        var teamId = Guid.NewGuid();
        _tasks.Setup(t => t.GetWeeklyThroughputByTeamAsync(teamId, default)).ReturnsAsync(new[]
        {
            new WeeklyThroughputPoint(new DateOnly(2026, 9, 8), 0),
            new WeeklyThroughputPoint(new DateOnly(2026, 9, 15), 5),
        });

        var value = await CreateProvider().GetCurrentValueAsync(AlertMetric.TeamVelocityChange, AlertScopeType.Team, teamId, default);

        value.Should().Be(0);
    }

    [Fact]
    public async Task CheckInCompliance_is_100_when_nobody_on_the_team_is_expected_to_check_in()
    {
        var teamId = Guid.NewGuid();
        var pm = Engineer.Create("PM", "pm@test.io", "hash", Roles.ProjectManager, 20, 14);
        pm.AssignToTeam(teamId);
        _engineers.Setup(e => e.ListAllAsync(default)).ReturnsAsync(new[] { pm });

        var value = await CreateProvider().GetCurrentValueAsync(AlertMetric.CheckInCompliance, AlertScopeType.Team, teamId, default);

        value.Should().Be(100);
    }

    [Fact]
    public async Task CheckInCompliance_divides_checked_in_by_roster_size()
    {
        var teamId = Guid.NewGuid();
        var checkedIn = Engineer.Create("Checked in", "ci@test.io", "hash", Roles.Engineer, 20, 14);
        checkedIn.AssignToTeam(teamId);
        var notCheckedIn = Engineer.Create("Not checked in", "nci@test.io", "hash", Roles.Engineer, 20, 14);
        notCheckedIn.AssignToTeam(teamId);

        _engineers.Setup(e => e.ListAllAsync(default)).ReturnsAsync(new[] { checkedIn, notCheckedIn });
        _checkIns.Setup(c => c.GetCheckInCountByDateRangeAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), default))
            .ReturnsAsync(new Dictionary<Guid, int> { [checkedIn.Id] = 3 });

        var value = await CreateProvider().GetCurrentValueAsync(AlertMetric.CheckInCompliance, AlertScopeType.Team, teamId, default);

        value.Should().Be(50);
    }

    [Fact]
    public async Task QaRejectRate_is_zero_when_nothing_has_been_sent_to_qa()
    {
        var projectId = Guid.NewGuid();
        var member = Guid.NewGuid();
        _projects.Setup(p => p.ListMembersAsync(projectId, default)).ReturnsAsync(new[]
        {
            new ProjectMemberDto(member, "Dev", "dev@test.io", Roles.Engineer, DateTime.UtcNow),
        });
        _tasks.Setup(t => t.GetPerformanceStatsAsync(member, It.IsAny<DateTime>(), It.IsAny<DateTime>(), projectId, default))
            .ReturnsAsync(new TaskPerformanceStats(0, 0, 0, 0, null, 0, 0));

        var value = await CreateProvider().GetCurrentValueAsync(AlertMetric.QaRejectRate, AlertScopeType.Project, projectId, default);

        value.Should().Be(0);
    }

    [Fact]
    public async Task BlockerCountChange_computes_percent_change_against_the_closest_snapshot_from_a_week_ago()
    {
        var projectId = Guid.NewGuid();
        _tasks.Setup(t => t.GetBlockedTasksAsync(default)).ReturnsAsync(new[]
        {
            BlockedTask(projectId, Guid.NewGuid()),
            BlockedTask(projectId, Guid.NewGuid()),
            BlockedTask(projectId, Guid.NewGuid()),
        }); // 3 blocked today
        _snapshots
            .Setup(s => s.GetMostRecentBeforeAsync(AlertMetric.BlockerCount, AlertScopeType.Project, projectId, It.IsAny<DateTime>(), default))
            .ReturnsAsync(AlertMetricSnapshot.Create(AlertMetric.BlockerCount, AlertScopeType.Project, projectId, 2)); // 2 blocked a week ago

        var value = await CreateProvider().GetCurrentValueAsync(AlertMetric.BlockerCountChange, AlertScopeType.Project, projectId, default);

        value.Should().Be(50); // 3 vs 2 is a 50% increase
    }

    [Fact]
    public async Task BlockerCountChange_records_a_BlockerCount_snapshot_as_a_side_effect()
    {
        var projectId = Guid.NewGuid();
        _tasks.Setup(t => t.GetBlockedTasksAsync(default)).ReturnsAsync(Array.Empty<PulseTask>());

        await CreateProvider().GetCurrentValueAsync(AlertMetric.BlockerCountChange, AlertScopeType.Project, projectId, default);

        _snapshots.Verify(s => s.RecordAsync(
            It.Is<AlertMetricSnapshot>(snap => snap.Metric == AlertMetric.BlockerCount && snap.ScopeType == AlertScopeType.Project && snap.ScopeId == projectId),
            default), Times.Once);
    }

    [Fact]
    public async Task BlockerCountChange_is_zero_when_there_is_no_snapshot_far_enough_back_yet()
    {
        var projectId = Guid.NewGuid();
        _tasks.Setup(t => t.GetBlockedTasksAsync(default)).ReturnsAsync(new[] { BlockedTask(projectId, Guid.NewGuid()) });
        // Default loose-mock setup already returns null — no snapshot recorded yet.

        var value = await CreateProvider().GetCurrentValueAsync(AlertMetric.BlockerCountChange, AlertScopeType.Project, projectId, default);

        value.Should().Be(0);
    }

    [Fact]
    public async Task BlockerCountChange_is_zero_when_the_prior_snapshot_was_zero()
    {
        var projectId = Guid.NewGuid();
        _tasks.Setup(t => t.GetBlockedTasksAsync(default)).ReturnsAsync(new[] { BlockedTask(projectId, Guid.NewGuid()) });
        _snapshots
            .Setup(s => s.GetMostRecentBeforeAsync(AlertMetric.BlockerCount, AlertScopeType.Project, projectId, It.IsAny<DateTime>(), default))
            .ReturnsAsync(AlertMetricSnapshot.Create(AlertMetric.BlockerCount, AlertScopeType.Project, projectId, 0));

        var value = await CreateProvider().GetCurrentValueAsync(AlertMetric.BlockerCountChange, AlertScopeType.Project, projectId, default);

        value.Should().Be(0);
    }

    [Fact]
    public async Task CheckInComplianceChange_computes_percent_change_against_the_closest_snapshot()
    {
        var teamId = Guid.NewGuid();
        var checkedIn = Engineer.Create("Checked in", "ci2@test.io", "hash", Roles.Engineer, 20, 14);
        checkedIn.AssignToTeam(teamId);
        _engineers.Setup(e => e.ListAllAsync(default)).ReturnsAsync(new[] { checkedIn });
        _checkIns.Setup(c => c.GetCheckInCountByDateRangeAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), default))
            .ReturnsAsync(new Dictionary<Guid, int> { [checkedIn.Id] = 1 }); // 100% today
        _snapshots
            .Setup(s => s.GetMostRecentBeforeAsync(AlertMetric.CheckInCompliance, AlertScopeType.Team, teamId, It.IsAny<DateTime>(), default))
            .ReturnsAsync(AlertMetricSnapshot.Create(AlertMetric.CheckInCompliance, AlertScopeType.Team, teamId, 50)); // 50% a week ago

        var value = await CreateProvider().GetCurrentValueAsync(AlertMetric.CheckInComplianceChange, AlertScopeType.Team, teamId, default);

        value.Should().Be(100); // 100 vs 50 is a 100% increase
    }

    [Fact]
    public async Task QaRejectRateChange_computes_percent_change_against_the_closest_snapshot()
    {
        var projectId = Guid.NewGuid();
        var member = Guid.NewGuid();
        _projects.Setup(p => p.ListMembersAsync(projectId, default)).ReturnsAsync(new[]
        {
            new ProjectMemberDto(member, "Dev", "dev2@test.io", Roles.Engineer, DateTime.UtcNow),
        });
        _tasks.Setup(t => t.GetPerformanceStatsAsync(member, It.IsAny<DateTime>(), It.IsAny<DateTime>(), projectId, default))
            .ReturnsAsync(new TaskPerformanceStats(0, 0, 0, 0, null, 10, 4)); // 40% today
        _snapshots
            .Setup(s => s.GetMostRecentBeforeAsync(AlertMetric.QaRejectRate, AlertScopeType.Project, projectId, It.IsAny<DateTime>(), default))
            .ReturnsAsync(AlertMetricSnapshot.Create(AlertMetric.QaRejectRate, AlertScopeType.Project, projectId, 20)); // 20% a week ago

        var value = await CreateProvider().GetCurrentValueAsync(AlertMetric.QaRejectRateChange, AlertScopeType.Project, projectId, default);

        value.Should().Be(100); // 40 vs 20 is a 100% increase
    }

    [Fact]
    public async Task QaRejectRate_sums_sent_and_rejected_across_every_project_member()
    {
        var projectId = Guid.NewGuid();
        var memberA = Guid.NewGuid();
        var memberB = Guid.NewGuid();
        _projects.Setup(p => p.ListMembersAsync(projectId, default)).ReturnsAsync(new[]
        {
            new ProjectMemberDto(memberA, "A", "a@test.io", Roles.Engineer, DateTime.UtcNow),
            new ProjectMemberDto(memberB, "B", "b@test.io", Roles.Engineer, DateTime.UtcNow),
        });
        _tasks.Setup(t => t.GetPerformanceStatsAsync(memberA, It.IsAny<DateTime>(), It.IsAny<DateTime>(), projectId, default))
            .ReturnsAsync(new TaskPerformanceStats(0, 0, 0, 0, null, 5, 1));
        _tasks.Setup(t => t.GetPerformanceStatsAsync(memberB, It.IsAny<DateTime>(), It.IsAny<DateTime>(), projectId, default))
            .ReturnsAsync(new TaskPerformanceStats(0, 0, 0, 0, null, 5, 2));

        var value = await CreateProvider().GetCurrentValueAsync(AlertMetric.QaRejectRate, AlertScopeType.Project, projectId, default);

        value.Should().Be(30); // 3 rejected / 10 sent = 30%
    }
}
