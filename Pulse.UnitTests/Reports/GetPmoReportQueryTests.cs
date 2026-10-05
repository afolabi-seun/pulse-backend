using Pulse.Application.Common.Interfaces;
using Pulse.Application.Overwork;
using Pulse.Application.Reports.Queries;
using Pulse.Domain.Engineers;
using Pulse.Domain.Projects;
using Pulse.Domain.Teams;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Reports;

public class GetPmoReportQueryTests
{
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IProjectRepository> _projects = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<ISprintRepository> _sprints = new();
    private readonly Mock<ICheckInRepository> _checkIns = new();
    private readonly Mock<ITimeEntryRepository> _timeEntries = new();
    private readonly Mock<IOverworkOverrideRepository> _overrides = new();
    private readonly Mock<IProjectFollowRepository> _follows = new();
    private readonly OverworkSignalsCalculator _calculator = new(new OverworkThresholds());
    private readonly Mock<IDepartmentThresholdRepository> _departmentThresholds = new();

    private EngineerWorkloadAssessor Assessor =>
        new(_calculator, _overrides.Object, _departmentThresholds.Object, _teams.Object);
    private readonly OverworkThresholds _thresholds = new();

    private GetPmoReportHandler CreateHandler() => new(
        _engineers.Object, _tasks.Object, _projects.Object, _teams.Object, _sprints.Object,
        _checkIns.Object, _timeEntries.Object, _follows.Object, Assessor, _thresholds);

    private void SetupMinimalData(Team team, Project project)
    {
        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync(Array.Empty<Engineer>());
        _tasks.Setup(r => r.GetAllActiveAsync(default)).ReturnsAsync(Array.Empty<Domain.Tasks.PulseTask>());
        _tasks.Setup(r => r.GetBlockedTasksAsync(default)).ReturnsAsync(Array.Empty<Domain.Tasks.PulseTask>());
        _overrides.Setup(r => r.GetAllActiveAsync(default)).ReturnsAsync([]);
        _departmentThresholds.Setup(r => r.GetAllAsync(default)).ReturnsAsync([]);
        _teams.Setup(r => r.ListAllAsync(default)).ReturnsAsync([team]);
        _projects.Setup(r => r.ListActiveAsync(default)).ReturnsAsync([project]);
        _tasks.Setup(r => r.GetEscalationCandidatesAsync(It.IsAny<int>(), default)).ReturnsAsync(Array.Empty<Domain.Tasks.PulseTask>());
        _checkIns.Setup(r => r.GetCheckInCountByDateRangeAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), default))
            .ReturnsAsync(new Dictionary<Guid, int>());
        _timeEntries.Setup(r => r.GetDeliveryHoursByEngineerInRangeAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<IReadOnlyList<Guid>>(), default))
            .ReturnsAsync(new Dictionary<Guid, decimal>());
        _timeEntries.Setup(r => r.GetHoursByProjectInRangeAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<IReadOnlyList<Guid>>(), default))
            .ReturnsAsync(new Dictionary<Guid, decimal>());
        _projects.Setup(r => r.GetMemberProjectIdsAsync(It.IsAny<Guid>(), default)).ReturnsAsync(new HashSet<Guid>());
        _follows.Setup(r => r.GetFollowedProjectIdsAsync(It.IsAny<Guid>(), default)).ReturnsAsync(Array.Empty<Guid>());
        _sprints.Setup(r => r.ListByTeamAsync(null, default)).ReturnsAsync([]);
        _tasks.Setup(r => r.GetProjectTaskCountsBatchAsync(It.IsAny<IReadOnlyList<Guid>>(), default))
            .ReturnsAsync(new Dictionary<Guid, ProjectTaskCounts> { [project.Id] = new(0, 0, 0, 0, 0, null, null) });
        _tasks.Setup(r => r.GetBySprintIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), default)).ReturnsAsync(new Dictionary<Guid, IReadOnlyList<Domain.Tasks.PulseTask>>());
        _tasks.Setup(r => r.GetDeliveredPointsInRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), default)).ReturnsAsync(0);
        _tasks.Setup(r => r.GetDeliveredPointsInRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<IReadOnlyList<Guid>>(), default)).ReturnsAsync(0);
        _tasks.Setup(r => r.GetCompletedTaskCountByEngineerInRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), default))
            .ReturnsAsync(new Dictionary<Guid, int>());
        _tasks.Setup(r => r.GetSubtaskCompletionCountByEngineerInRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), default))
            .ReturnsAsync(new Dictionary<Guid, int>());
    }

    [Fact]
    public async Task ProjectManager_with_no_team_still_sees_org_wide_teams_and_projects()
    {
        var team = Team.Create("Falcons");
        var project = Project.Create("Alpha");
        SetupMinimalData(team, project);

        var result = await CreateHandler().Handle(
            new GetPmoReportQuery(Roles.ProjectManager, Guid.NewGuid()), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Teams.Should().ContainSingle(t => t.TeamId == team.Id);
        result.Data.Projects.Should().ContainSingle(p => p.ProjectId == project.Id);
        // A caller with no team must never be looked up as an engineer to resolve a team scope —
        // that's exactly the path that produced an always-empty report before this fix.
        _engineers.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), default), Times.Never);
    }

    [Fact]
    public async Task HeadOfPmo_sees_org_wide_teams_and_projects()
    {
        var team = Team.Create("Falcons");
        var project = Project.Create("Alpha");
        SetupMinimalData(team, project);

        var result = await CreateHandler().Handle(
            new GetPmoReportQuery(Roles.HeadOfPmo, Guid.NewGuid()), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Teams.Should().ContainSingle(t => t.TeamId == team.Id);
        result.Data.Projects.Should().ContainSingle(p => p.ProjectId == project.Id);
    }

    [Fact]
    public async Task TeamLead_is_scoped_to_their_own_team_only()
    {
        var team = Team.Create("Falcons");
        var otherTeam = Team.Create("Hawks");
        var project = Project.Create("Alpha");
        var lead = Engineer.Create("Lead", "lead@x.io", "hash", Roles.TeamLead, 5, 5);
        lead.AssignToTeam(team.Id);

        SetupMinimalData(team, project);
        _teams.Setup(r => r.ListAllAsync(default)).ReturnsAsync([team, otherTeam]);
        _engineers.Setup(r => r.GetByIdAsync(lead.Id, default)).ReturnsAsync(lead);

        var result = await CreateHandler().Handle(
            new GetPmoReportQuery(Roles.TeamLead, lead.Id), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Teams.Should().ContainSingle(t => t.TeamId == team.Id);
        result.Data.Teams.Should().NotContain(t => t.TeamId == otherTeam.Id);
    }

    [Fact]
    public async Task Team_utilization_and_check_in_compliance_exclude_roles_that_can_never_carry_a_delivery_workload()
    {
        var team = Team.Create("Falcons");
        var project = Project.Create("Alpha");
        var engineer = Engineer.Create("Dev", "dev@x.io", "hash", Roles.Engineer, 20, 14);
        var pmoOnTeam = Engineer.Create("Pmo", "pmo@x.io", "hash", Roles.HeadOfPmo, 20, 14);
        engineer.AssignToTeam(team.Id);
        pmoOnTeam.AssignToTeam(team.Id);

        SetupMinimalData(team, project);
        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync([engineer, pmoOnTeam]);

        var result = await CreateHandler().Handle(
            new GetPmoReportQuery(Roles.HeadOfPmo, Guid.NewGuid()), default);

        result.IsSuccess.Should().BeTrue();
        var teamEntry = result.Data!.Teams.Should().ContainSingle(t => t.TeamId == team.Id).Subject;
        teamEntry.Engineers.Should().ContainSingle(e => e.EngineerId == engineer.Id);
        teamEntry.Engineers.Should().NotContain(e => e.EngineerId == pmoOnTeam.Id);

        var complianceEntry = result.Data.CheckInCompliance.Should().ContainSingle(c => c.TeamId == team.Id).Subject;
        complianceEntry.Weeks.Should().OnlyContain(w => w.EngineerCount == 1);
    }

    [Fact]
    public async Task Engineer_entry_carries_completed_task_count_from_the_repository()
    {
        var team = Team.Create("Falcons");
        var project = Project.Create("Alpha");
        var engineer = Engineer.Create("Dev", "dev@x.io", "hash", Roles.Engineer, 20, 14);
        engineer.AssignToTeam(team.Id);

        SetupMinimalData(team, project);
        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync([engineer]);
        _tasks.Setup(r => r.GetCompletedTaskCountByEngineerInRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), default))
            .ReturnsAsync(new Dictionary<Guid, int> { [engineer.Id] = 3 });

        var result = await CreateHandler().Handle(
            new GetPmoReportQuery(Roles.HeadOfPmo, Guid.NewGuid()), default);

        result.IsSuccess.Should().BeTrue();
        var teamEntry = result.Data!.Teams.Should().ContainSingle(t => t.TeamId == team.Id).Subject;
        teamEntry.Engineers.Should().ContainSingle(e => e.EngineerId == engineer.Id).Which.CompletedTasks.Should().Be(3);
    }

    [Fact]
    public async Task Team_utilization_hours_come_from_delivery_only_not_the_all_category_total()
    {
        // A week with real delivery work plus a chunk of approved leave — the all-category total
        // would blend both in; the report should only reflect the delivery hours.
        var team = Team.Create("Falcons");
        var project = Project.Create("Alpha");
        var engineer = Engineer.Create("Dev", "dev@x.io", "hash", Roles.Engineer, 20, 14);
        engineer.AssignToTeam(team.Id);

        SetupMinimalData(team, project);
        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync([engineer]);
        _timeEntries.Setup(r => r.GetDeliveryHoursByEngineerInRangeAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<IReadOnlyList<Guid>>(), default))
            .ReturnsAsync(new Dictionary<Guid, decimal> { [engineer.Id] = 12m });
        _timeEntries.Setup(r => r.GetHoursByEngineerInRangeAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<IReadOnlyList<Guid>>(), default))
            .ReturnsAsync(new Dictionary<Guid, decimal> { [engineer.Id] = 44m }); // 12h delivery + 32h leave, if it were ever blended in

        var result = await CreateHandler().Handle(
            new GetPmoReportQuery(Roles.HeadOfPmo, Guid.NewGuid()), default);

        result.IsSuccess.Should().BeTrue();
        var teamEntry = result.Data!.Teams.Should().ContainSingle(t => t.TeamId == team.Id).Subject;
        teamEntry.Engineers.Should().ContainSingle(e => e.EngineerId == engineer.Id).Which.HoursLoggedThisWeek.Should().Be(12m);
    }

    [Fact]
    public async Task Avg_load_is_null_not_zero_when_the_team_has_nobody_delivery_eligible_left()
    {
        // The only real way a team's utilization roster ends up empty today: every member is a
        // role the delivery-roster filter already excludes (e.g. a ProjectManager sitting on a
        // team). BaselinePoints itself can never be <= 0 for a real Engineer (Engineer.Create
        // rejects it), so an empty roster — not a bad baseline — is the reachable trigger.
        var team = Team.Create("Falcons");
        var project = Project.Create("Alpha");
        var pmOnTeam = Engineer.Create("Pm", "pm_only_team@x.io", "hash", Roles.ProjectManager, 10, 14);
        pmOnTeam.AssignToTeam(team.Id);

        SetupMinimalData(team, project);
        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync([pmOnTeam]);

        var result = await CreateHandler().Handle(
            new GetPmoReportQuery(Roles.HeadOfPmo, Guid.NewGuid()), default);

        result.IsSuccess.Should().BeTrue();
        var teamEntry = result.Data!.Teams.Should().ContainSingle(t => t.TeamId == team.Id).Subject;
        teamEntry.Engineers.Should().BeEmpty();
        teamEntry.AvgLoadPct.Should().BeNull();
    }

    [Fact]
    public async Task An_explicit_from_and_to_scopes_hours_the_same_as_delivered_points()
    {
        // Previously hours stayed pinned to the calendar week containing "to" no matter what range
        // was requested — "Points delivered" would reflect a custom range while every Hours column
        // on the same report silently kept showing just one week, with nothing indicating that.
        var team = Team.Create("Falcons");
        var project = Project.Create("Alpha");
        SetupMinimalData(team, project);

        var from = new DateOnly(2026, 1, 1);
        var to = new DateOnly(2026, 3, 31); // a quarter, not aligned to any single calendar week
        _tasks.Setup(r => r.GetDeliveredPointsInRangeAsync(
                It.Is<DateTime>(d => d.Date == from.ToDateTime(TimeOnly.MinValue).Date),
                It.Is<DateTime>(d => d.Date == to.ToDateTime(TimeOnly.MinValue).Date), default))
            .ReturnsAsync(42);

        var result = await CreateHandler().Handle(
            new GetPmoReportQuery(Roles.HeadOfPmo, Guid.NewGuid(), From: from, To: to), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.DeliveredFrom.Should().Be(from.ToString("yyyy-MM-dd"));
        result.Data.DeliveredTo.Should().Be(to.ToString("yyyy-MM-dd"));
        result.Data.TotalDeliveredPoints.Should().Be(42);
        _timeEntries.Verify(r => r.GetDeliveryHoursByEngineerInRangeAsync(from, to, null, default), Times.Once);
        _timeEntries.Verify(r => r.GetHoursByProjectInRangeAsync(from, to, null, default), Times.Once);
    }

    [Fact]
    public async Task PMO_gets_the_org_wide_avg_cycle_time_unscoped_by_project()
    {
        var team = Team.Create("Falcons");
        var project = Project.Create("Alpha");
        SetupMinimalData(team, project);
        _tasks.Setup(r => r.GetAvgCycleTimeDaysInRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), default))
            .ReturnsAsync(3.5);

        var result = await CreateHandler().Handle(
            new GetPmoReportQuery(Roles.HeadOfPmo, Guid.NewGuid()), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.AvgCycleTimeDays.Should().Be(3.5);
        _tasks.Verify(r => r.GetAvgCycleTimeDaysInRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<IReadOnlyList<Guid>>(), default), Times.Never);
    }

    [Fact]
    public async Task A_team_lead_gets_cycle_time_scoped_to_their_own_projects_and_null_reads_as_nothing_completed()
    {
        var team = Team.Create("Falcons");
        var project = Project.Create("Alpha", ownerTeamId: team.Id);
        var lead = Engineer.Create("Lead", "lead@x.io", "hash", Roles.TeamLead, 5, 5);
        lead.AssignToTeam(team.Id);

        SetupMinimalData(team, project);
        _teams.Setup(r => r.ListAllAsync(default)).ReturnsAsync([team]);
        _engineers.Setup(r => r.GetByIdAsync(lead.Id, default)).ReturnsAsync(lead);
        _tasks.Setup(r => r.GetAvgCycleTimeDaysInRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<IReadOnlyList<Guid>>(), default))
            .ReturnsAsync((double?)null);

        var result = await CreateHandler().Handle(
            new GetPmoReportQuery(Roles.TeamLead, lead.Id), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.AvgCycleTimeDays.Should().BeNull();
        _tasks.Verify(r => r.GetAvgCycleTimeDaysInRangeAsync(
            It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.Is<IReadOnlyList<Guid>>(ids => ids.Contains(project.Id)), default), Times.Once);
    }

    [Fact]
    public async Task PMO_gets_the_org_wide_avg_pr_approval_time_unscoped_by_project()
    {
        var team = Team.Create("Falcons");
        var project = Project.Create("Alpha");
        SetupMinimalData(team, project);
        _tasks.Setup(r => r.GetAvgPrApprovalHoursInRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), default))
            .ReturnsAsync(4.25);

        var result = await CreateHandler().Handle(
            new GetPmoReportQuery(Roles.HeadOfPmo, Guid.NewGuid()), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.AvgPrApprovalHours.Should().Be(4.25);
        _tasks.Verify(r => r.GetAvgPrApprovalHoursInRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<IReadOnlyList<Guid>>(), default), Times.Never);
    }

    [Fact]
    public async Task A_team_lead_gets_pr_approval_time_scoped_to_their_own_projects_and_null_reads_as_nothing_approved()
    {
        var team = Team.Create("Falcons");
        var project = Project.Create("Alpha", ownerTeamId: team.Id);
        var lead = Engineer.Create("Lead", "lead@x.io", "hash", Roles.TeamLead, 5, 5);
        lead.AssignToTeam(team.Id);

        SetupMinimalData(team, project);
        _teams.Setup(r => r.ListAllAsync(default)).ReturnsAsync([team]);
        _engineers.Setup(r => r.GetByIdAsync(lead.Id, default)).ReturnsAsync(lead);
        _tasks.Setup(r => r.GetAvgPrApprovalHoursInRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<IReadOnlyList<Guid>>(), default))
            .ReturnsAsync((double?)null);

        var result = await CreateHandler().Handle(
            new GetPmoReportQuery(Roles.TeamLead, lead.Id), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.AvgPrApprovalHours.Should().BeNull();
        _tasks.Verify(r => r.GetAvgPrApprovalHoursInRangeAsync(
            It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.Is<IReadOnlyList<Guid>>(ids => ids.Contains(project.Id)), default), Times.Once);
    }

    [Fact]
    public async Task ProjectHealth_carries_the_high_priority_open_task_count_through()
    {
        var team = Team.Create("Falcons");
        var project = Project.Create("Alpha");
        SetupMinimalData(team, project);
        _tasks.Setup(r => r.GetProjectTaskCountsBatchAsync(It.IsAny<IReadOnlyList<Guid>>(), default))
            .ReturnsAsync(new Dictionary<Guid, ProjectTaskCounts> { [project.Id] = new(0, 0, 0, 0, 0, null, null, HighPriorityOpenCount: 3) });

        var result = await CreateHandler().Handle(
            new GetPmoReportQuery(Roles.ProjectManager, Guid.NewGuid()), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Projects.Should().ContainSingle(p => p.ProjectId == project.Id && p.HighPriorityOpenTasks == 3);
    }
}
