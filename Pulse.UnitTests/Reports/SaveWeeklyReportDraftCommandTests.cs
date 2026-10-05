using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Overwork;
using Pulse.Application.Reports.Commands;
using Pulse.Domain.Engineers;
using Pulse.Domain.Projects;
using Pulse.Domain.Reports;
using Pulse.Domain.Tasks;
using Pulse.Domain.Teams;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Reports;

public class SaveWeeklyReportDraftCommandTests
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

    private SaveWeeklyReportDraftHandler CreateHandler() => new(
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
        _tasks.Setup(r => r.GetDeliveredPointsInRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<IReadOnlyList<Guid>>(), default))
            .ReturnsAsync(0);
    }

    [Fact]
    public async Task Non_team_lead_non_head_cannot_save_a_draft()
    {
        var cmd = new SaveWeeklyReportDraftCommand(Guid.NewGuid(), Roles.ProjectManager, Guid.NewGuid(),
            DateOnly.FromDateTime(DateTime.UtcNow), "summary", "wins", "next", "notes");

        var result = await CreateHandler().Handle(cmd, default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
        _engineers.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), default), Times.Never);
    }

    [Fact]
    public async Task HeadOfPmo_can_save_a_draft_for_any_team()
    {
        var actorId = Guid.NewGuid();
        var team = Team.Create("Falcons");
        var teamId = team.Id;
        _weeklyReports.Setup(r => r.GetByTeamAndWeekAsync(teamId, It.IsAny<DateOnly>(), default)).ReturnsAsync((WeeklyReport?)null);
        SetupMinimalDigest(teamId, team);

        var cmd = new SaveWeeklyReportDraftCommand(actorId, Roles.HeadOfPmo, teamId,
            DateOnly.FromDateTime(DateTime.UtcNow), "summary", "wins", "next", "notes");

        var result = await CreateHandler().Handle(cmd, default);

        result.IsSuccess.Should().BeTrue();
        _engineers.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), default), Times.Never);
    }

    [Fact]
    public async Task HeadOfRnD_can_save_a_draft_for_a_team_in_their_department()
    {
        var actorId = Guid.NewGuid();
        var team = Team.Create("Falcons");
        var teamId = team.Id;
        _access.Setup(a => a.CanAccessTeamAsync(teamId, actorId, Roles.HeadOfRnD, default)).ReturnsAsync(true);
        _weeklyReports.Setup(r => r.GetByTeamAndWeekAsync(teamId, It.IsAny<DateOnly>(), default)).ReturnsAsync((WeeklyReport?)null);
        SetupMinimalDigest(teamId, team);

        var cmd = new SaveWeeklyReportDraftCommand(actorId, Roles.HeadOfRnD, teamId,
            DateOnly.FromDateTime(DateTime.UtcNow), "summary", "wins", "next", "notes");

        var result = await CreateHandler().Handle(cmd, default);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task HeadOfRnD_cannot_save_a_draft_outside_their_department()
    {
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        _access.Setup(a => a.CanAccessTeamAsync(teamId, actorId, Roles.HeadOfRnD, default)).ReturnsAsync(false);

        var cmd = new SaveWeeklyReportDraftCommand(actorId, Roles.HeadOfRnD, teamId,
            DateOnly.FromDateTime(DateTime.UtcNow), "summary", "wins", "next", "notes");

        var result = await CreateHandler().Handle(cmd, default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
        _weeklyReports.Verify(r => r.AddAsync(It.IsAny<WeeklyReport>(), default), Times.Never);
    }

    [Fact]
    public async Task Team_lead_of_a_different_team_cannot_save_the_draft()
    {
        var actorId = Guid.NewGuid();
        var ownTeamId = Guid.NewGuid();
        var otherTeamId = Guid.NewGuid();

        var caller = Engineer.Create("Lead", "lead@ex.com", "hash", Roles.TeamLead, 10, 5);
        caller.AssignToTeam(ownTeamId);
        _engineers.Setup(r => r.GetByIdAsync(actorId, default)).ReturnsAsync(caller);

        var cmd = new SaveWeeklyReportDraftCommand(actorId, Roles.TeamLead, otherTeamId,
            DateOnly.FromDateTime(DateTime.UtcNow), "summary", "wins", "next", "notes");

        var result = await CreateHandler().Handle(cmd, default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
        _weeklyReports.Verify(r => r.AddAsync(It.IsAny<WeeklyReport>(), default), Times.Never);
    }

    [Fact]
    public async Task Team_lead_can_create_a_new_draft_for_their_own_team()
    {
        var actorId = Guid.NewGuid();
        var team = Team.Create("Falcons");
        var teamId = team.Id;

        var caller = Engineer.Create("Lead", "lead@ex.com", "hash", Roles.TeamLead, 10, 5);
        caller.AssignToTeam(teamId);
        _engineers.Setup(r => r.GetByIdAsync(actorId, default)).ReturnsAsync(caller);
        _weeklyReports.Setup(r => r.GetByTeamAndWeekAsync(teamId, It.IsAny<DateOnly>(), default)).ReturnsAsync((WeeklyReport?)null);
        SetupMinimalDigest(teamId, team);

        var cmd = new SaveWeeklyReportDraftCommand(actorId, Roles.TeamLead, teamId,
            DateOnly.FromDateTime(DateTime.UtcNow), "Shipped the widget", "Fixed 3 bugs", "Ship v2", "Fully staffed");

        var result = await CreateHandler().Handle(cmd, default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.ExecutiveSummary.Should().Be("Shipped the widget");
        result.Data!.IsNew.Should().BeFalse();
        _weeklyReports.Verify(r => r.AddAsync(It.IsAny<WeeklyReport>(), default), Times.Once);
        _weeklyReports.Verify(r => r.SaveChangesAsync(default), Times.Once);
    }

    [Fact]
    public async Task Editing_a_submitted_report_clears_its_sign_off()
    {
        var actorId = Guid.NewGuid();
        var team = Team.Create("Falcons");
        var teamId = team.Id;
        var weekOf = DateOnly.FromDateTime(DateTime.UtcNow);
        var weekStart = WeekOf.Monday(weekOf);

        var caller = Engineer.Create("Lead", "lead@ex.com", "hash", Roles.TeamLead, 10, 5);
        caller.AssignToTeam(teamId);
        _engineers.Setup(r => r.GetByIdAsync(actorId, default)).ReturnsAsync(caller);

        var existing = WeeklyReport.Create(teamId, weekStart, actorId);
        existing.UpdateDraft("old summary", "old wins", "old next", "old notes", actorId);
        existing.Submit(actorId);
        _weeklyReports.Setup(r => r.GetByTeamAndWeekAsync(teamId, weekStart, default)).ReturnsAsync(existing);
        SetupMinimalDigest(teamId, team);

        var cmd = new SaveWeeklyReportDraftCommand(actorId, Roles.TeamLead, teamId, weekOf,
            "new summary", "new wins", "new next", "new notes");

        var result = await CreateHandler().Handle(cmd, default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.ExecutiveSummary.Should().Be("new summary");
        result.Data!.SubmittedAt.Should().BeNull();
        result.Data!.SubmittedById.Should().BeNull();
        _weeklyReports.Verify(r => r.AddAsync(It.IsAny<WeeklyReport>(), default), Times.Never);
    }
}
