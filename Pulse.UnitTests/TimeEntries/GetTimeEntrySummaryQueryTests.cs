using Pulse.Application.Common.Interfaces;
using Pulse.Application.TimeEntries.Queries;
using Pulse.Domain.Engineers;
using Pulse.Domain.Teams;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.TimeEntries;

public class GetTimeEntrySummaryQueryTests
{
    private readonly Mock<ITimeEntryRepository> _timeEntries = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<IProjectRepository> _projects = new();

    public GetTimeEntrySummaryQueryTests()
    {
        _timeEntries.Setup(r => r.GetHoursByProjectInRangeAsync(
                It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<IReadOnlyList<Guid>>(), default))
            .ReturnsAsync(new Dictionary<Guid, decimal>());
        _timeEntries.Setup(r => r.GetDailyHoursByEngineerInRangeAsync(
                It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<IReadOnlyList<Guid>>(), default))
            .ReturnsAsync(new Dictionary<(Guid, DateOnly), decimal>());
        _projects.Setup(r => r.GetNamesByIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), default))
            .ReturnsAsync(new Dictionary<Guid, string>());
    }

    private GetTimeEntrySummaryHandler CreateHandler() => new(_timeEntries.Object, _engineers.Object, _teams.Object, _projects.Object);

    [Fact]
    public async Task Team_lead_only_sees_engineers_on_their_own_team()
    {
        var teamId = Guid.NewGuid();
        var otherTeamId = Guid.NewGuid();
        var lead = Engineer.Create("Lead", "lead@x.io", "hash", Roles.TeamLead, 5, 5);
        lead.AssignToTeam(teamId);
        var teammate = Engineer.Create("Teammate", "mate@x.io", "hash", Roles.Engineer, 5, 5);
        teammate.AssignToTeam(teamId);
        var stranger = Engineer.Create("Stranger", "stranger@x.io", "hash", Roles.Engineer, 5, 5);
        stranger.AssignToTeam(otherTeamId);

        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync([lead, teammate, stranger]);
        _timeEntries.Setup(r => r.GetHoursByEngineerInRangeAsync(
                It.IsAny<DateOnly>(), It.IsAny<DateOnly>(),
                It.Is<IReadOnlyList<Guid>>(ids => ids.Count == 2 && ids.Contains(lead.Id) && ids.Contains(teammate.Id)),
                default))
            .ReturnsAsync(new Dictionary<Guid, decimal> { [lead.Id] = 3, [teammate.Id] = 5 });

        var result = await CreateHandler().Handle(new GetTimeEntrySummaryQuery(null, Roles.TeamLead, lead.Id), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Engineers.Should().HaveCount(2);
        result.Data!.Engineers.Should().NotContain(e => e.EngineerId == stranger.Id);
        result.Data!.TeamTotalHours.Should().Be(8);
    }

    [Fact]
    public async Task PMO_sees_every_active_engineer_org_wide()
    {
        var e1 = Engineer.Create("A", "a@x.io", "hash", Roles.Engineer, 5, 5);
        var e2 = Engineer.Create("B", "b@x.io", "hash", Roles.Engineer, 5, 5);

        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync([e1, e2]);
        _timeEntries.Setup(r => r.GetHoursByEngineerInRangeAsync(
                It.IsAny<DateOnly>(), It.IsAny<DateOnly>(),
                It.Is<IReadOnlyList<Guid>>(ids => ids.Count == 2), default))
            .ReturnsAsync(new Dictionary<Guid, decimal> { [e1.Id] = 1, [e2.Id] = 2 });

        var result = await CreateHandler().Handle(new GetTimeEntrySummaryQuery(null, Roles.HeadOfPmo, Guid.NewGuid()), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Engineers.Should().HaveCount(2);
        result.Data!.TeamTotalHours.Should().Be(3);
    }

    [Fact]
    public async Task Executive_sees_every_active_engineer_org_wide()
    {
        var e1 = Engineer.Create("A", "a2@x.io", "hash", Roles.Engineer, 5, 5);
        var e2 = Engineer.Create("B", "b2@x.io", "hash", Roles.Engineer, 5, 5);

        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync([e1, e2]);
        _timeEntries.Setup(r => r.GetHoursByEngineerInRangeAsync(
                It.IsAny<DateOnly>(), It.IsAny<DateOnly>(),
                It.Is<IReadOnlyList<Guid>>(ids => ids.Count == 2), default))
            .ReturnsAsync(new Dictionary<Guid, decimal> { [e1.Id] = 4, [e2.Id] = 6 });

        var result = await CreateHandler().Handle(new GetTimeEntrySummaryQuery(null, Roles.Executive, Guid.NewGuid()), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Engineers.Should().HaveCount(2);
        result.Data!.TeamTotalHours.Should().Be(10);
    }

    [Fact]
    public async Task Department_head_is_scoped_to_teams_in_their_own_department()
    {
        var dept = "Engineering";
        var head = Engineer.Create("Head", "head@x.io", "hash", Roles.HeadOfRnD, 5, 5);
        var inDept = Engineer.Create("InDept", "in@x.io", "hash", Roles.Engineer, 5, 5);
        var otherDept = Engineer.Create("OtherDept", "other@x.io", "hash", Roles.Engineer, 5, 5);

        var headTeam = Team.Create("R&D Team", head.Id);
        headTeam.SetDepartment(dept);
        var otherTeam = Team.Create("Design Team", otherDept.Id);
        otherTeam.SetDepartment("Design");

        head.AssignToTeam(headTeam.Id);
        inDept.AssignToTeam(headTeam.Id);
        otherDept.AssignToTeam(otherTeam.Id);

        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync([head, inDept, otherDept]);
        _teams.Setup(r => r.GetByIdAsync(headTeam.Id, default)).ReturnsAsync(headTeam);
        _teams.Setup(r => r.ListAllAsync(default)).ReturnsAsync([headTeam, otherTeam]);
        _timeEntries.Setup(r => r.GetHoursByEngineerInRangeAsync(
                It.IsAny<DateOnly>(), It.IsAny<DateOnly>(),
                It.Is<IReadOnlyList<Guid>>(ids => ids.Count == 2 && ids.Contains(head.Id) && ids.Contains(inDept.Id)),
                default))
            .ReturnsAsync(new Dictionary<Guid, decimal>());

        var result = await CreateHandler().Handle(new GetTimeEntrySummaryQuery(null, Roles.HeadOfRnD, head.Id), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Engineers.Should().HaveCount(2);
        result.Data!.Engineers.Should().NotContain(e => e.EngineerId == otherDept.Id);
    }

    [Fact]
    public async Task Each_engineer_gets_a_seven_day_daily_breakdown_covering_monday_to_sunday()
    {
        var e1 = Engineer.Create("A", "a3@x.io", "hash", Roles.Engineer, 5, 5);
        var monday = new DateOnly(2026, 9, 21); // a Monday
        var wednesday = monday.AddDays(2);

        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync([e1]);
        _timeEntries.Setup(r => r.GetHoursByEngineerInRangeAsync(
                It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<IReadOnlyList<Guid>>(), default))
            .ReturnsAsync(new Dictionary<Guid, decimal> { [e1.Id] = 4 });
        _timeEntries.Setup(r => r.GetDailyHoursByEngineerInRangeAsync(
                monday, monday.AddDays(6), It.IsAny<IReadOnlyList<Guid>>(), default))
            .ReturnsAsync(new Dictionary<(Guid, DateOnly), decimal> { [(e1.Id, wednesday)] = 4 });

        var result = await CreateHandler().Handle(new GetTimeEntrySummaryQuery(monday, Roles.Executive, Guid.NewGuid()), default);

        result.IsSuccess.Should().BeTrue();
        var daily = result.Data!.Engineers.Single().DailyHours;
        daily.Should().HaveCount(7);
        daily.Select(d => d.Date).Should().Equal(Enumerable.Range(0, 7).Select(monday.AddDays));
        daily.Single(d => d.Date == wednesday).Hours.Should().Be(4);
        daily.Where(d => d.Date != wednesday).Should().OnlyContain(d => d.Hours == 0);
    }

    [Fact]
    public async Task Roles_that_can_never_log_time_are_excluded_from_the_roster()
    {
        // Executive, HeadOfPmo and ProjectManager are excluded from TimeEntrySubmitter — they'd
        // otherwise sit in the roster forever at a permanent 0h. Accountant (like HR) was added to
        // it deliberately, so it stays on the roster.
        var engineer = Engineer.Create("Engineer", "eng@x.io", "hash", Roles.Engineer, 5, 5);
        var exec = Engineer.Create("Exec", "exec@x.io", "hash", Roles.Executive, 5, 5);
        var pmo = Engineer.Create("Pmo", "pmo@x.io", "hash", Roles.HeadOfPmo, 5, 5);
        var pm = Engineer.Create("Pm", "pm@x.io", "hash", Roles.ProjectManager, 5, 5);
        var accountant = Engineer.Create("Accountant", "acct@x.io", "hash", Roles.Accountant, 5, 5);

        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync([engineer, exec, pmo, pm, accountant]);
        _timeEntries.Setup(r => r.GetHoursByEngineerInRangeAsync(
                It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<IReadOnlyList<Guid>>(), default))
            .ReturnsAsync(new Dictionary<Guid, decimal> { [engineer.Id] = 5 });

        var result = await CreateHandler().Handle(new GetTimeEntrySummaryQuery(null, Roles.Executive, Guid.NewGuid()), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Engineers.Select(e => e.EngineerId).Should().BeEquivalentTo(new[] { engineer.Id, accountant.Id });
    }

    [Fact]
    public async Task An_explicit_from_and_to_overrides_the_weekOf_window_with_an_arbitrary_range()
    {
        var e1 = Engineer.Create("A", "a4@x.io", "hash", Roles.Engineer, 5, 5);
        var from = new DateOnly(2026, 8, 31);
        var to = from.AddDays(11); // spans two calendar weeks — not Monday-Sunday-aligned

        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync([e1]);
        _timeEntries.Setup(r => r.GetHoursByEngineerInRangeAsync(from, to, It.IsAny<IReadOnlyList<Guid>>(), default))
            .ReturnsAsync(new Dictionary<Guid, decimal> { [e1.Id] = 7 });
        _timeEntries.Setup(r => r.GetDailyHoursByEngineerInRangeAsync(from, to, It.IsAny<IReadOnlyList<Guid>>(), default))
            .ReturnsAsync(new Dictionary<(Guid, DateOnly), decimal>());

        var result = await CreateHandler().Handle(
            new GetTimeEntrySummaryQuery(WeekOf: null, Roles.Executive, Guid.NewGuid(), From: from, To: to), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.WeekOf.Should().Be(from.ToString("yyyy-MM-dd"));
        result.Data!.To.Should().Be(to.ToString("yyyy-MM-dd"));
        result.Data!.Engineers.Single().DailyHours.Should().HaveCount(12);
        result.Data!.TeamTotalHours.Should().Be(7);
    }

    [Fact]
    public async Task A_to_before_from_is_rejected()
    {
        var from = new DateOnly(2026, 9, 10);
        var to = from.AddDays(-1);

        var result = await CreateHandler().Handle(
            new GetTimeEntrySummaryQuery(WeekOf: null, Roles.Executive, Guid.NewGuid(), From: from, To: to), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("VALIDATION_ERROR");
    }

    [Fact]
    public async Task A_range_over_the_max_is_rejected()
    {
        var from = new DateOnly(2026, 1, 1);
        var to = from.AddDays(63);

        var result = await CreateHandler().Handle(
            new GetTimeEntrySummaryQuery(WeekOf: null, Roles.Executive, Guid.NewGuid(), From: from, To: to), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("VALIDATION_ERROR");
    }
}
