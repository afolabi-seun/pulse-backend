using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Performance.Queries;
using Pulse.Application.Projects;
using Pulse.Domain.Engineers;
using Pulse.Domain.Projects;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Performance;

public class GetMyPerformanceHandlerTests
{
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IEscalationEventRepository> _escalationEvents = new();
    private readonly Mock<ICheckInRepository> _checkIns = new();

    public GetMyPerformanceHandlerTests()
    {
        _tasks
            .Setup(t => t.GetPerformanceStatsAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TaskPerformanceStats(10, 2, 2, 2, 3.0, 1, 0));
        _escalationEvents
            .Setup(e => e.GetEscalatedTaskCountAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        _checkIns
            .Setup(c => c.GetCheckInCountByDateRangeAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, int>());
    }

    private GetMyPerformanceHandler CreateHandler() =>
        new(_engineers.Object, _tasks.Object, _escalationEvents.Object, _checkIns.Object);

    [Fact]
    public async Task Returns_NOT_FOUND_when_engineer_missing()
    {
        _engineers.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((Engineer?)null);

        var result = await CreateHandler().Handle(new GetMyPerformanceQuery(Guid.NewGuid()), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task Returns_metrics_for_the_caller()
    {
        var engineer = Engineer.Create("Dev", "dev@pulse.io", "pw", Roles.Engineer, 20, 14);
        _engineers.Setup(r => r.GetByIdAsync(engineer.Id, default)).ReturnsAsync(engineer);

        var result = await CreateHandler().Handle(new GetMyPerformanceQuery(engineer.Id), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.EngineerId.Should().Be(engineer.Id);
        result.Data!.DeliveredPoints.Should().Be(10);
    }
}

public class GetTeamPerformanceHandlerTests
{
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IEscalationEventRepository> _escalationEvents = new();
    private readonly Mock<ICheckInRepository> _checkIns = new();
    private readonly Mock<IProjectAccessPolicy> _access = new();

    public GetTeamPerformanceHandlerTests()
    {
        _tasks
            .Setup(t => t.GetPerformanceStatsAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TaskPerformanceStats(5, 1, 1, 1, 2.0, 0, 0));
        _escalationEvents
            .Setup(e => e.GetEscalatedTaskCountAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        _checkIns
            .Setup(c => c.GetCheckInCountByDateRangeAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, int>());
    }

    private GetTeamPerformanceHandler CreateHandler() =>
        new(_engineers.Object, _tasks.Object, _escalationEvents.Object, _checkIns.Object, _access.Object);

    [Fact]
    public async Task Returns_FORBIDDEN_when_actor_cannot_access_team()
    {
        _access
            .Setup(a => a.CanAccessTeamAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await CreateHandler().Handle(
            new GetTeamPerformanceQuery(Guid.NewGuid(), null, Guid.NewGuid(), Roles.Engineer), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
    }

    [Fact]
    public async Task Returns_one_entry_per_team_member()
    {
        var teamId = Guid.NewGuid();
        var e1 = Engineer.Create("Dev One", "one@pulse.io", "pw", Roles.Engineer, 20, 14);
        var e2 = Engineer.Create("Dev Two", "two@pulse.io", "pw", Roles.Engineer, 20, 14);
        _access
            .Setup(a => a.CanAccessTeamAsync(teamId, It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _engineers
            .Setup(r => r.ListWithWorkloadAsync(null, teamId, default))
            .ReturnsAsync(new[] { (e1, 0, 0), (e2, 0, 0) });

        var result = await CreateHandler().Handle(
            new GetTeamPerformanceQuery(teamId, null, Guid.NewGuid(), Roles.TeamLead), default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Should().HaveCount(2);
        result.Data!.Select(d => d.EngineerId).Should().BeEquivalentTo([e1.Id, e2.Id]);
    }

    [Fact]
    public async Task Excludes_a_role_that_can_never_carry_a_delivery_workload()
    {
        var teamId = Guid.NewGuid();
        var engineer = Engineer.Create("Dev", "dev@pulse.io", "pw", Roles.Engineer, 20, 14);
        var pmOnTeam = Engineer.Create("Pm", "pm@pulse.io", "pw", Roles.ProjectManager, 20, 14);
        _access
            .Setup(a => a.CanAccessTeamAsync(teamId, It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _engineers
            .Setup(r => r.ListWithWorkloadAsync(null, teamId, default))
            .ReturnsAsync(new[] { (engineer, 0, 0), (pmOnTeam, 0, 0) });

        var result = await CreateHandler().Handle(
            new GetTeamPerformanceQuery(teamId, null, Guid.NewGuid(), Roles.TeamLead), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Select(d => d.EngineerId).Should().BeEquivalentTo([engineer.Id]);
    }

    [Fact]
    public async Task Executive_bypasses_CanAccessTeamAsync_entirely()
    {
        // Executive has no team of its own, so CanAccessTeamAsync's IC fallback branch would
        // always return false for it — this must never be called for Executive at all.
        var teamId = Guid.NewGuid();
        _access
            .Setup(a => a.CanAccessTeamAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _engineers
            .Setup(r => r.ListWithWorkloadAsync(null, teamId, default))
            .ReturnsAsync(Array.Empty<(Engineer, int, int)>());

        var result = await CreateHandler().Handle(
            new GetTeamPerformanceQuery(teamId, null, Guid.NewGuid(), Roles.Executive), default);

        result.IsSuccess.Should().BeTrue();
        _access.Verify(a => a.CanAccessTeamAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Hr_bypasses_CanAccessTeamAsync_entirely()
    {
        // Same rationale as Executive above — HR also has no team of its own.
        var teamId = Guid.NewGuid();
        _access
            .Setup(a => a.CanAccessTeamAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _engineers
            .Setup(r => r.ListWithWorkloadAsync(null, teamId, default))
            .ReturnsAsync(Array.Empty<(Engineer, int, int)>());

        var result = await CreateHandler().Handle(
            new GetTeamPerformanceQuery(teamId, null, Guid.NewGuid(), Roles.HR), default);

        result.IsSuccess.Should().BeTrue();
        _access.Verify(a => a.CanAccessTeamAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}

public class GetProjectPerformanceHandlerTests
{
    private readonly Mock<IProjectRepository> _projects = new();
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IEscalationEventRepository> _escalationEvents = new();
    private readonly Mock<IProjectAccessPolicy> _access = new();

    public GetProjectPerformanceHandlerTests()
    {
        _access
            .Setup(a => a.CanAccessProjectAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _escalationEvents
            .Setup(e => e.GetEscalatedTaskCountAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
    }

    private GetProjectPerformanceHandler CreateHandler() =>
        new(_projects.Object, _tasks.Object, _escalationEvents.Object, _access.Object);

    [Fact]
    public async Task Returns_NOT_FOUND_when_project_missing()
    {
        _projects.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((Project?)null);

        var result = await CreateHandler().Handle(new GetProjectPerformanceQuery(Guid.NewGuid(), Guid.NewGuid(), Roles.ProjectManager), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task Aggregates_delivered_points_across_members()
    {
        var project = Project.Create("Test project");
        var member1 = Guid.NewGuid();
        var member2 = Guid.NewGuid();
        _projects.Setup(r => r.GetByIdAsync(project.Id, default)).ReturnsAsync(project);
        _projects.Setup(r => r.ListMembersAsync(project.Id, default)).ReturnsAsync(new[]
        {
            new ProjectMemberDto(member1, "One", "one@pulse.io", Roles.Engineer, DateTime.UtcNow),
            new ProjectMemberDto(member2, "Two", "two@pulse.io", Roles.Engineer, DateTime.UtcNow),
        });
        _tasks
            .Setup(t => t.GetPerformanceStatsAsync(member1, It.IsAny<DateTime>(), It.IsAny<DateTime>(), project.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TaskPerformanceStats(10, 2, 2, 2, 2.0, 1, 0));
        _tasks
            .Setup(t => t.GetPerformanceStatsAsync(member2, It.IsAny<DateTime>(), It.IsAny<DateTime>(), project.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TaskPerformanceStats(5, 1, 1, 0, 4.0, 0, 0));

        var result = await CreateHandler().Handle(new GetProjectPerformanceQuery(project.Id, Guid.NewGuid(), Roles.ProjectManager), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.EngineerCount.Should().Be(2);
        result.Data!.DeliveredPoints.Should().Be(15);
        result.Data!.TasksCompleted.Should().Be(3);
        result.Data!.TasksCompletedOnTime.Should().Be(2);
        // Weighted by each member's own task count (2 tasks @ 2.0d, 1 task @ 4.0d), not a plain
        // average of the two per-engineer averages (which would give the wrong 3.0d) — (2*2.0 +
        // 1*4.0) / 3 = 8/3.
        result.Data!.AvgCycleTimeDays.Should().BeApproximately(8.0 / 3.0, 0.0001);
    }

    [Fact]
    public async Task Cycle_time_is_weighted_by_task_count_not_averaged_across_engineers()
    {
        // A single low-volume engineer must not swing the project's figure as much as a
        // high-volume one — 1 task at 20 days vs 30 tasks at 2 days should land close to 2 days,
        // not the naive (20+2)/2 = 11d a plain average-of-averages would produce.
        var project = Project.Create("Weighted cycle time project");
        var lowVolume = Guid.NewGuid();
        var highVolume = Guid.NewGuid();
        _projects.Setup(r => r.GetByIdAsync(project.Id, default)).ReturnsAsync(project);
        _projects.Setup(r => r.ListMembersAsync(project.Id, default)).ReturnsAsync(new[]
        {
            new ProjectMemberDto(lowVolume, "Low", "low@pulse.io", Roles.Engineer, DateTime.UtcNow),
            new ProjectMemberDto(highVolume, "High", "high@pulse.io", Roles.Engineer, DateTime.UtcNow),
        });
        _tasks
            .Setup(t => t.GetPerformanceStatsAsync(lowVolume, It.IsAny<DateTime>(), It.IsAny<DateTime>(), project.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TaskPerformanceStats(0, 1, 1, 1, 20.0, 0, 0));
        _tasks
            .Setup(t => t.GetPerformanceStatsAsync(highVolume, It.IsAny<DateTime>(), It.IsAny<DateTime>(), project.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TaskPerformanceStats(0, 30, 30, 30, 2.0, 0, 0));

        var result = await CreateHandler().Handle(new GetProjectPerformanceQuery(project.Id, Guid.NewGuid(), Roles.ProjectManager), default);

        result.IsSuccess.Should().BeTrue();
        // (1*20.0 + 30*2.0) / 31 = 80/31 ≈ 2.58 — close to the high-volume engineer's own 2.0d,
        // not the midpoint 11d a naive average-of-averages would give.
        result.Data!.AvgCycleTimeDays.Should().BeApproximately(80.0 / 31.0, 0.0001);
    }

    [Fact]
    public async Task OnTimeRate_is_null_across_the_project_when_no_member_had_a_dated_task()
    {
        var project = Project.Create("No due dates project");
        var member = Guid.NewGuid();
        _projects.Setup(r => r.GetByIdAsync(project.Id, default)).ReturnsAsync(project);
        _projects.Setup(r => r.ListMembersAsync(project.Id, default)).ReturnsAsync(new[]
        {
            new ProjectMemberDto(member, "Dev", "dev@pulse.io", Roles.Engineer, DateTime.UtcNow),
        });
        _tasks
            .Setup(t => t.GetPerformanceStatsAsync(member, It.IsAny<DateTime>(), It.IsAny<DateTime>(), project.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TaskPerformanceStats(10, 3, TasksWithDueDate: 0, TasksCompletedOnTime: 0, null, 0, 0));

        var result = await CreateHandler().Handle(new GetProjectPerformanceQuery(project.Id, Guid.NewGuid(), Roles.ProjectManager), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.OnTimeRate.Should().BeNull();
    }

    [Fact]
    public async Task Executive_bypasses_CanAccessProjectAsync_entirely()
    {
        var project = Project.Create("Test project");
        _access
            .Setup(a => a.CanAccessProjectAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _projects.Setup(r => r.GetByIdAsync(project.Id, default)).ReturnsAsync(project);
        _projects.Setup(r => r.ListMembersAsync(project.Id, default)).ReturnsAsync(Array.Empty<ProjectMemberDto>());

        var result = await CreateHandler().Handle(new GetProjectPerformanceQuery(project.Id, Guid.NewGuid(), Roles.Executive), default);

        result.IsSuccess.Should().BeTrue();
        _access.Verify(a => a.CanAccessProjectAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Hr_bypasses_CanAccessProjectAsync_entirely()
    {
        var project = Project.Create("Test project");
        _access
            .Setup(a => a.CanAccessProjectAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _projects.Setup(r => r.GetByIdAsync(project.Id, default)).ReturnsAsync(project);
        _projects.Setup(r => r.ListMembersAsync(project.Id, default)).ReturnsAsync(Array.Empty<ProjectMemberDto>());

        var result = await CreateHandler().Handle(new GetProjectPerformanceQuery(project.Id, Guid.NewGuid(), Roles.HR), default);

        result.IsSuccess.Should().BeTrue();
        _access.Verify(a => a.CanAccessProjectAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
