using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Overwork;
using Pulse.Application.Reports.Queries;
using Pulse.Domain.Engineers;
using Pulse.Domain.Projects;
using Pulse.Domain.Reports;
using Pulse.Domain.Tasks;
using Pulse.Domain.Teams;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Reports;

public class GetWeeklyReportQueryTests
{
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<IProjectRepository> _projects = new();
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<ICheckInRepository> _checkIns = new();
    private readonly Mock<ITimeEntryRepository> _timeEntries = new();
    private readonly Mock<IOverworkOverrideRepository> _overrides = new();
    private readonly Mock<IWeeklyReportRepository> _weeklyReports = new();
    private readonly Mock<IProjectAccessPolicy> _access = new();
    private readonly OverworkSignalsCalculator _calculator = new(new OverworkThresholds());
    private readonly Mock<IDepartmentThresholdRepository> _departmentThresholds = new();

    private EngineerWorkloadAssessor Assessor =>
        new(_calculator, _overrides.Object, _departmentThresholds.Object, _teams.Object);
    private readonly OverworkThresholds _thresholds = new();

    private GetWeeklyReportHandler CreateHandler() => new(
        _engineers.Object, _teams.Object, _projects.Object, _tasks.Object,
        _checkIns.Object, _timeEntries.Object, _weeklyReports.Object, _access.Object, Assessor, _thresholds);

    private void SetupMinimalDigest(Guid teamId, Team team)
    {
        _teams.Setup(r => r.GetByIdAsync(teamId, default)).ReturnsAsync(team);
        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync(Array.Empty<Engineer>());
        _projects.Setup(r => r.ListActiveAsync(default)).ReturnsAsync(Array.Empty<Project>());
        _tasks.Setup(r => r.GetBlockedTasksAsync(default)).ReturnsAsync(Array.Empty<PulseTask>());
        _tasks.Setup(r => r.GetAllActiveAsync(default)).ReturnsAsync(Array.Empty<PulseTask>());
        _overrides.Setup(r => r.GetAllActiveAsync(default)).ReturnsAsync([]);
        _departmentThresholds.Setup(r => r.GetAllAsync(default)).ReturnsAsync([]);
        _tasks.Setup(r => r.GetEscalationCandidatesAsync(It.IsAny<int>(), default)).ReturnsAsync(Array.Empty<PulseTask>());
        _checkIns.Setup(r => r.GetCheckInCountByDateRangeAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), default))
            .ReturnsAsync(new Dictionary<Guid, int>());
        _checkIns.Setup(r => r.GetCheckedInEngineersByProjectAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), default))
            .ReturnsAsync(new Dictionary<Guid, HashSet<Guid>>());
        _timeEntries.Setup(r => r.GetDeliveryHoursByEngineerInRangeAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<IReadOnlyList<Guid>>(), default))
            .ReturnsAsync(new Dictionary<Guid, decimal>());
        _timeEntries.Setup(r => r.GetHoursByProjectInRangeAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<IReadOnlyList<Guid>>(), default))
            .ReturnsAsync(new Dictionary<Guid, decimal>());
        _weeklyReports.Setup(r => r.GetByTeamAndWeekAsync(teamId, It.IsAny<DateOnly>(), default)).ReturnsAsync((WeeklyReport?)null);
        _tasks.Setup(r => r.GetDeliveredPointsInRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<IReadOnlyList<Guid>>(), default))
            .ReturnsAsync(0);
        _tasks.Setup(r => r.GetCompletedTaskCountByEngineerInRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), default))
            .ReturnsAsync(new Dictionary<Guid, int>());
        _tasks.Setup(r => r.GetSubtaskCompletionCountByEngineerInRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), default))
            .ReturnsAsync(new Dictionary<Guid, int>());
    }

    [Fact]
    public async Task Team_lead_can_view_own_teams_report_with_no_existing_draft()
    {
        var actorId = Guid.NewGuid();
        var team = Team.Create("Falcons");
        var teamId = team.Id;

        var caller = Engineer.Create("Lead", "lead@ex.com", "hash", Roles.TeamLead, 10, 5);
        caller.AssignToTeam(teamId);
        _engineers.Setup(r => r.GetByIdAsync(actorId, default)).ReturnsAsync(caller);
        SetupMinimalDigest(teamId, team);

        var result = await CreateHandler().Handle(new GetWeeklyReportQuery(actorId, Roles.TeamLead), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.TeamId.Should().Be(teamId);
        result.Data!.IsNew.Should().BeTrue();
        result.Data!.ExecutiveSummary.Should().BeEmpty();
        result.Data!.SubmittedAt.Should().BeNull();
    }

    [Fact]
    public async Task Team_lead_cannot_view_another_teams_report()
    {
        var actorId = Guid.NewGuid();
        var ownTeamId = Guid.NewGuid();
        var otherTeamId = Guid.NewGuid();

        var caller = Engineer.Create("Lead", "lead@ex.com", "hash", Roles.TeamLead, 10, 5);
        caller.AssignToTeam(ownTeamId);
        _engineers.Setup(r => r.GetByIdAsync(actorId, default)).ReturnsAsync(caller);

        var result = await CreateHandler().Handle(new GetWeeklyReportQuery(actorId, Roles.TeamLead, otherTeamId), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
    }

    [Fact]
    public async Task HeadOfPmo_without_teamId_gets_business_rule_violation()
    {
        var result = await CreateHandler().Handle(new GetWeeklyReportQuery(Guid.NewGuid(), Roles.HeadOfPmo), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
    }

    [Fact]
    public async Task HeadOfPmo_can_view_any_teams_report_without_being_a_member()
    {
        var team = Team.Create("Falcons");
        var teamId = team.Id;
        SetupMinimalDigest(teamId, team);

        var result = await CreateHandler().Handle(new GetWeeklyReportQuery(Guid.NewGuid(), Roles.HeadOfPmo, teamId), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.TeamId.Should().Be(teamId);
        _engineers.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), default), Times.Never);
    }

    [Fact]
    public async Task DepartmentHead_without_teamId_gets_business_rule_violation()
    {
        var result = await CreateHandler().Handle(new GetWeeklyReportQuery(Guid.NewGuid(), Roles.HeadOfRnD), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
    }

    [Fact]
    public async Task DepartmentHead_can_view_a_teams_report_in_their_own_department()
    {
        var actorId = Guid.NewGuid();
        var team = Team.Create("Falcons");
        var teamId = team.Id;
        _access.Setup(a => a.CanAccessTeamAsync(teamId, actorId, Roles.HeadOfRnD, default)).ReturnsAsync(true);
        SetupMinimalDigest(teamId, team);

        var result = await CreateHandler().Handle(new GetWeeklyReportQuery(actorId, Roles.HeadOfRnD, teamId), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.TeamId.Should().Be(teamId);
    }

    [Fact]
    public async Task HeadOfCoreBanking_can_view_a_teams_report_in_their_own_department()
    {
        var actorId = Guid.NewGuid();
        var team = Team.Create("Ledger Squad");
        var teamId = team.Id;
        _access.Setup(a => a.CanAccessTeamAsync(teamId, actorId, Roles.HeadOfCoreBanking, default)).ReturnsAsync(true);
        SetupMinimalDigest(teamId, team);

        var result = await CreateHandler().Handle(new GetWeeklyReportQuery(actorId, Roles.HeadOfCoreBanking, teamId), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.TeamId.Should().Be(teamId);
    }

    [Fact]
    public async Task HeadOfInfraDevOps_can_view_a_teams_report_in_their_own_department()
    {
        var actorId = Guid.NewGuid();
        var team = Team.Create("Platform Reliability Squad");
        var teamId = team.Id;
        _access.Setup(a => a.CanAccessTeamAsync(teamId, actorId, Roles.HeadOfInfraDevOps, default)).ReturnsAsync(true);
        SetupMinimalDigest(teamId, team);

        var result = await CreateHandler().Handle(new GetWeeklyReportQuery(actorId, Roles.HeadOfInfraDevOps, teamId), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.TeamId.Should().Be(teamId);
    }

    [Fact]
    public async Task DepartmentHead_cannot_view_a_teams_report_outside_their_department()
    {
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        _access.Setup(a => a.CanAccessTeamAsync(teamId, actorId, Roles.HeadOfRnD, default)).ReturnsAsync(false);

        var result = await CreateHandler().Handle(new GetWeeklyReportQuery(actorId, Roles.HeadOfRnD, teamId), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
    }

    [Fact]
    public async Task Utilization_and_compliance_exclude_a_role_that_can_never_carry_a_delivery_workload()
    {
        var team = Team.Create("Falcons");
        var teamId = team.Id;
        var engineer = Engineer.Create("Dev", "dev@ex.com", "hash", Roles.Engineer, 10, 5);
        engineer.AssignToTeam(teamId);
        var pmOnTeam = Engineer.Create("Pm", "pm@ex.com", "hash", Roles.ProjectManager, 10, 5);
        pmOnTeam.AssignToTeam(teamId);

        SetupMinimalDigest(teamId, team);
        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync(new[] { engineer, pmOnTeam });

        var result = await CreateHandler().Handle(new GetWeeklyReportQuery(Guid.NewGuid(), Roles.HeadOfPmo, teamId), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Utilization.Engineers.Should().ContainSingle(e => e.EngineerId == engineer.Id);
        result.Data.Utilization.Engineers.Should().NotContain(e => e.EngineerId == pmOnTeam.Id);
        result.Data.Compliance.Weeks.Should().OnlyContain(w => w.EngineerCount == 1);
    }

    [Fact]
    public async Task Avg_load_is_null_not_zero_when_the_team_has_nobody_delivery_eligible_left()
    {
        // A literal 0% here used to read as "this team is completely idle" — null makes clear
        // there's simply no one on the team a load figure could apply to.
        var team = Team.Create("Falcons");
        var teamId = team.Id;
        var pmOnTeam = Engineer.Create("Pm", "pm_only_weekly@ex.com", "hash", Roles.ProjectManager, 10, 5);
        pmOnTeam.AssignToTeam(teamId);

        SetupMinimalDigest(teamId, team);
        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync(new[] { pmOnTeam });

        var result = await CreateHandler().Handle(new GetWeeklyReportQuery(Guid.NewGuid(), Roles.HeadOfPmo, teamId), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Utilization.Engineers.Should().BeEmpty();
        result.Data.Utilization.AvgLoadPct.Should().BeNull();
    }

    // ── check-in coverage ────────────────────────────────────────────────────

    private void SetupSingleProjectWorkstream(Guid teamId, Project project, IReadOnlyList<PulseTask> activeTasks)
    {
        _projects.Setup(r => r.ListActiveAsync(default)).ReturnsAsync(new[] { project });
        _tasks.Setup(r => r.GetAllActiveAsync(default)).ReturnsAsync(activeTasks);
        _tasks.Setup(r => r.GetProjectTaskCountsAsync(project.Id, default))
            .ReturnsAsync(new ProjectTaskCounts(activeTasks.Count, 0, 0, activeTasks.Count, 0, null, null));
    }

    [Fact]
    public async Task Coverage_is_full_when_every_actively_assigned_engineer_checked_in_on_the_project()
    {
        var team = Team.Create("Falcons");
        var teamId = team.Id;
        var project = Project.Create("Alpha", ownerTeamId: teamId);
        var engA = Engineer.Create("Engineer A", "a@ex.com", "hash", Roles.Engineer, 10, 5);
        engA.AssignToTeam(teamId);
        var engB = Engineer.Create("Engineer B", "b@ex.com", "hash", Roles.Engineer, 10, 5);
        engB.AssignToTeam(teamId);
        var taskA = PulseTask.Create("A", 3, project.Id);
        taskA.Assign(engA.Id, Guid.NewGuid());
        var taskB = PulseTask.Create("B", 3, project.Id);
        taskB.Assign(engB.Id, Guid.NewGuid());

        SetupMinimalDigest(teamId, team);
        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync(new[] { engA, engB });
        SetupSingleProjectWorkstream(teamId, project, new[] { taskA, taskB });
        _checkIns.Setup(r => r.GetCheckedInEngineersByProjectAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), default))
            .ReturnsAsync(new Dictionary<Guid, HashSet<Guid>> { [project.Id] = new() { engA.Id, engB.Id } });

        var result = await CreateHandler().Handle(new GetWeeklyReportQuery(Guid.NewGuid(), Roles.HeadOfPmo, teamId), default);

        result.IsSuccess.Should().BeTrue();
        var coverage = result.Data!.CheckInCoverage.Should().ContainSingle().Subject;
        coverage.ExpectedEngineers.Should().Be(2);
        coverage.CheckedInEngineers.Should().Be(2);
        coverage.CoveragePct.Should().Be(100);
    }

    [Fact]
    public async Task Coverage_is_partial_when_only_some_actively_assigned_engineers_checked_in()
    {
        var team = Team.Create("Falcons");
        var teamId = team.Id;
        var project = Project.Create("Alpha", ownerTeamId: teamId);
        var engA = Engineer.Create("Engineer A", "a@ex.com", "hash", Roles.Engineer, 10, 5);
        engA.AssignToTeam(teamId);
        var engB = Engineer.Create("Engineer B", "b@ex.com", "hash", Roles.Engineer, 10, 5);
        engB.AssignToTeam(teamId);
        var taskA = PulseTask.Create("A", 3, project.Id);
        taskA.Assign(engA.Id, Guid.NewGuid());
        var taskB = PulseTask.Create("B", 3, project.Id);
        taskB.Assign(engB.Id, Guid.NewGuid());

        SetupMinimalDigest(teamId, team);
        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync(new[] { engA, engB });
        SetupSingleProjectWorkstream(teamId, project, new[] { taskA, taskB });
        // engB checked in on a different project — shouldn't count toward Alpha's coverage.
        _checkIns.Setup(r => r.GetCheckedInEngineersByProjectAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), default))
            .ReturnsAsync(new Dictionary<Guid, HashSet<Guid>> { [project.Id] = new() { engA.Id }, [Guid.NewGuid()] = new() { engB.Id } });

        var result = await CreateHandler().Handle(new GetWeeklyReportQuery(Guid.NewGuid(), Roles.HeadOfPmo, teamId), default);

        result.IsSuccess.Should().BeTrue();
        var coverage = result.Data!.CheckInCoverage.Should().ContainSingle().Subject;
        coverage.ExpectedEngineers.Should().Be(2);
        coverage.CheckedInEngineers.Should().Be(1);
        coverage.CoveragePct.Should().Be(50);
    }

    [Fact]
    public async Task Coverage_is_zero_percent_with_no_divide_by_zero_when_no_one_is_actively_assigned()
    {
        var team = Team.Create("Falcons");
        var teamId = team.Id;
        var project = Project.Create("Alpha", ownerTeamId: teamId);

        SetupMinimalDigest(teamId, team);
        SetupSingleProjectWorkstream(teamId, project, Array.Empty<PulseTask>());

        var result = await CreateHandler().Handle(new GetWeeklyReportQuery(Guid.NewGuid(), Roles.HeadOfPmo, teamId), default);

        result.IsSuccess.Should().BeTrue();
        var coverage = result.Data!.CheckInCoverage.Should().ContainSingle().Subject;
        coverage.ExpectedEngineers.Should().Be(0);
        coverage.CheckedInEngineers.Should().Be(0);
        coverage.CoveragePct.Should().Be(0);
    }

    // ── cross-team project scoping ──────────────────────────────────────────

    [Fact]
    public async Task Workstream_includes_a_cross_team_project_with_an_active_task_assigned_to_this_teams_engineer()
    {
        var team = Team.Create("Falcons");
        var teamId = team.Id;
        var otherTeamId = Guid.NewGuid();
        var ownedProject = Project.Create("Alpha", ownerTeamId: teamId);
        var sharedProject = Project.Create("Shared", ownerTeamId: otherTeamId);

        var eng = Engineer.Create("Engineer A", "a@ex.com", "hash", Roles.Engineer, 10, 5);
        eng.AssignToTeam(teamId);

        var ownedTask = PulseTask.Create("Owned work", 3, ownedProject.Id);
        ownedTask.Assign(eng.Id, Guid.NewGuid());
        var loanedTask = PulseTask.Create("Loaned work", 5, sharedProject.Id);
        loanedTask.Assign(eng.Id, Guid.NewGuid());

        SetupMinimalDigest(teamId, team);
        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync(new[] { eng });
        _projects.Setup(r => r.ListActiveAsync(default)).ReturnsAsync(new[] { ownedProject, sharedProject });
        _tasks.Setup(r => r.GetAllActiveAsync(default)).ReturnsAsync(new[] { ownedTask, loanedTask });
        // Owned project: unscoped, whole-project counts.
        _tasks.Setup(r => r.GetProjectTaskCountsAsync(ownedProject.Id, default))
            .ReturnsAsync(new ProjectTaskCounts(1, 0, 0, 1, 0, null, null));
        // Shared project: only this team's slice (1 active task), NOT the whole project's, which
        // would be 2 if another team's task on it were counted too.
        _tasks.Setup(r => r.GetProjectTaskCountsAsync(sharedProject.Id, It.Is<IReadOnlyList<Guid>>(ids => ids.Count == 1 && ids.Contains(eng.Id)), default))
            .ReturnsAsync(new ProjectTaskCounts(1, 0, 0, 1, 0, null, null));

        var result = await CreateHandler().Handle(new GetWeeklyReportQuery(Guid.NewGuid(), Roles.HeadOfPmo, teamId), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Workstreams.Select(w => w.Name).Should().BeEquivalentTo(new[] { "Alpha", "Shared" });
        var shared = result.Data!.Workstreams.Single(w => w.Name == "Shared");
        shared.ActiveTasks.Should().Be(1);
    }

    [Fact]
    public async Task Delivered_points_are_scoped_to_this_teams_engineers_not_the_whole_shared_project()
    {
        var team = Team.Create("Falcons");
        var teamId = team.Id;
        var otherTeamId = Guid.NewGuid();
        var sharedProject = Project.Create("Shared", ownerTeamId: otherTeamId);

        var eng = Engineer.Create("Engineer A", "a@ex.com", "hash", Roles.Engineer, 10, 5);
        eng.AssignToTeam(teamId);
        var loanedTask = PulseTask.Create("Loaned work", 5, sharedProject.Id);
        loanedTask.Assign(eng.Id, Guid.NewGuid());

        SetupMinimalDigest(teamId, team);
        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync(new[] { eng });
        _projects.Setup(r => r.ListActiveAsync(default)).ReturnsAsync(new[] { sharedProject });
        _tasks.Setup(r => r.GetAllActiveAsync(default)).ReturnsAsync(new[] { loanedTask });
        _tasks.Setup(r => r.GetProjectTaskCountsAsync(sharedProject.Id, It.IsAny<IReadOnlyList<Guid>>(), default))
            .ReturnsAsync(new ProjectTaskCounts(1, 0, 0, 1, 0, null, null));
        // Only this team's engineer id should be passed — not every assignee on the shared project.
        _tasks.Setup(r => r.GetDeliveredPointsInRangeByAssigneesAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(),
                It.Is<IReadOnlyList<Guid>>(ids => ids.Count == 1 && ids.Contains(eng.Id)), default))
            .ReturnsAsync(7);

        var result = await CreateHandler().Handle(new GetWeeklyReportQuery(Guid.NewGuid(), Roles.HeadOfPmo, teamId), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.TotalDeliveredPoints.Should().Be(7);
    }
}
