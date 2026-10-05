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

public class SubmitWeeklyReportCommandTests
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

    private SubmitWeeklyReportHandler CreateHandler() => new(
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
    public async Task Non_team_lead_non_head_cannot_submit()
    {
        var cmd = new SubmitWeeklyReportCommand(Guid.NewGuid(), Roles.ProjectManager, Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow));

        var result = await CreateHandler().Handle(cmd, default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
    }

    [Fact]
    public async Task HeadOfPmo_can_submit_an_existing_draft_for_any_team()
    {
        var actorId = Guid.NewGuid();
        var team = Team.Create("Falcons");
        var teamId = team.Id;
        var weekOf = DateOnly.FromDateTime(DateTime.UtcNow);
        var weekStart = WeekOf.Monday(weekOf);

        var draft = WeeklyReport.Create(teamId, weekStart, Guid.NewGuid());
        draft.UpdateDraft("summary", "wins", "next", "notes", Guid.NewGuid());
        _weeklyReports.Setup(r => r.GetByTeamAndWeekAsync(teamId, weekStart, default)).ReturnsAsync(draft);
        SetupMinimalDigest(teamId, team);

        var cmd = new SubmitWeeklyReportCommand(actorId, Roles.HeadOfPmo, teamId, weekOf);

        var result = await CreateHandler().Handle(cmd, default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.SubmittedById.Should().Be(actorId);
        _engineers.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), default), Times.Never);
    }

    [Fact]
    public async Task HeadOfRnD_cannot_submit_outside_their_department()
    {
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        _access.Setup(a => a.CanAccessTeamAsync(teamId, actorId, Roles.HeadOfRnD, default)).ReturnsAsync(false);

        var cmd = new SubmitWeeklyReportCommand(actorId, Roles.HeadOfRnD, teamId, DateOnly.FromDateTime(DateTime.UtcNow));

        var result = await CreateHandler().Handle(cmd, default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
    }

    [Fact]
    public async Task Team_lead_of_a_different_team_cannot_submit()
    {
        var actorId = Guid.NewGuid();
        var ownTeamId = Guid.NewGuid();
        var otherTeamId = Guid.NewGuid();

        var caller = Engineer.Create("Lead", "lead@ex.com", "hash", Roles.TeamLead, 10, 5);
        caller.AssignToTeam(ownTeamId);
        _engineers.Setup(r => r.GetByIdAsync(actorId, default)).ReturnsAsync(caller);

        var cmd = new SubmitWeeklyReportCommand(actorId, Roles.TeamLead, otherTeamId, DateOnly.FromDateTime(DateTime.UtcNow));

        var result = await CreateHandler().Handle(cmd, default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
    }

    [Fact]
    public async Task Returns_not_found_when_no_draft_exists_for_the_week()
    {
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        var caller = Engineer.Create("Lead", "lead@ex.com", "hash", Roles.TeamLead, 10, 5);
        caller.AssignToTeam(teamId);
        _engineers.Setup(r => r.GetByIdAsync(actorId, default)).ReturnsAsync(caller);
        _weeklyReports.Setup(r => r.GetByTeamAndWeekAsync(teamId, It.IsAny<DateOnly>(), default)).ReturnsAsync((WeeklyReport?)null);

        var cmd = new SubmitWeeklyReportCommand(actorId, Roles.TeamLead, teamId, DateOnly.FromDateTime(DateTime.UtcNow));

        var result = await CreateHandler().Handle(cmd, default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task Team_lead_can_submit_an_existing_draft()
    {
        var actorId = Guid.NewGuid();
        var team = Team.Create("Falcons");
        var teamId = team.Id;
        var weekOf = DateOnly.FromDateTime(DateTime.UtcNow);
        var weekStart = WeekOf.Monday(weekOf);

        var caller = Engineer.Create("Lead", "lead@ex.com", "hash", Roles.TeamLead, 10, 5);
        caller.AssignToTeam(teamId);
        _engineers.Setup(r => r.GetByIdAsync(actorId, default)).ReturnsAsync(caller);

        var draft = WeeklyReport.Create(teamId, weekStart, actorId);
        draft.UpdateDraft("summary", "wins", "next", "notes", actorId);
        _weeklyReports.Setup(r => r.GetByTeamAndWeekAsync(teamId, weekStart, default)).ReturnsAsync(draft);
        SetupMinimalDigest(teamId, team);

        var cmd = new SubmitWeeklyReportCommand(actorId, Roles.TeamLead, teamId, weekOf);

        var result = await CreateHandler().Handle(cmd, default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.SubmittedById.Should().Be(actorId);
        result.Data!.SubmittedAt.Should().NotBeNull();
        _weeklyReports.Verify(r => r.SaveChangesAsync(default), Times.Once);
    }
}
