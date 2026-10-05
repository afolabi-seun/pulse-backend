using Pulse.Application.CheckIns.Queries;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.CheckIns;
using Pulse.Domain.Engineers;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.CheckIns;

public class GetStandupSummaryHandlerTests
{
    private readonly Mock<ICheckInRepository> _checkIns = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<IProjectRepository> _projects = new();

    private GetStandupSummaryHandler CreateHandler() =>
        new(_checkIns.Object, _engineers.Object, _teams.Object, _projects.Object);

    private static Engineer NewEngineer(string name, string role) =>
        Engineer.Create(name, $"{name.ToLower()}@test.io", "hash", role, 20, 14);

    [Fact]
    public async Task Missing_excludes_a_role_not_expected_to_check_in()
    {
        var engineer = NewEngineer("Dev", Roles.Engineer);
        var hr = NewEngineer("Hr", Roles.HR);
        _engineers.Setup(e => e.ListActiveAsync(default)).ReturnsAsync(new[] { engineer, hr });
        _checkIns.Setup(c => c.GetByDateAsync(It.IsAny<DateOnly>(), It.IsAny<IReadOnlyList<Guid>>(), default))
            .ReturnsAsync(Array.Empty<CheckIn>());

        // Caller is HR themselves — org-wide scope, sees every engineer including other HR/Exec people.
        var result = await CreateHandler().Handle(new GetStandupSummaryQuery(null, null, Roles.HR, hr.Id), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.MissingEngineers.Should().ContainSingle(m => m.Id == engineer.Id);
        result.Data.MissingEngineers.Should().NotContain(m => m.Id == hr.Id);
    }

    [Fact]
    public async Task Missing_still_includes_a_delivery_role_without_a_checkin()
    {
        var engineer = NewEngineer("Dev", Roles.Engineer);
        _engineers.Setup(e => e.ListActiveAsync(default)).ReturnsAsync(new[] { engineer });
        _checkIns.Setup(c => c.GetByDateAsync(It.IsAny<DateOnly>(), It.IsAny<IReadOnlyList<Guid>>(), default))
            .ReturnsAsync(Array.Empty<CheckIn>());

        var result = await CreateHandler().Handle(new GetStandupSummaryQuery(null, null, Roles.Executive, Guid.NewGuid()), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.MissingEngineers.Should().ContainSingle(m => m.Id == engineer.Id);
    }

    [Fact]
    public async Task Entries_still_shows_a_checkin_submitted_by_a_non_expected_role()
    {
        // Submission itself isn't role-gated — if HR/Exec/PMO submit one anyway, it should still
        // show under Entries. Only the Missing list is narrowed by this fix.
        var hr = NewEngineer("Hr", Roles.HR);
        _engineers.Setup(e => e.ListActiveAsync(default)).ReturnsAsync(new[] { hr });
        var checkIn = CheckIn.Submit(hr.Id, DateOnly.FromDateTime(DateTime.UtcNow), "Reviewed feedback", "More review", null);
        _checkIns.Setup(c => c.GetByDateAsync(It.IsAny<DateOnly>(), It.IsAny<IReadOnlyList<Guid>>(), default))
            .ReturnsAsync(new[] { checkIn });

        var result = await CreateHandler().Handle(new GetStandupSummaryQuery(null, null, Roles.HR, hr.Id), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Entries.Items.Should().ContainSingle(e => e.EngineerId == hr.Id);
        result.Data.MissingEngineers.Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_excludes_ProjectManager_and_HeadOfPmo_too()
    {
        var pm = NewEngineer("Pm", Roles.ProjectManager);
        var pmo = NewEngineer("Pmo", Roles.HeadOfPmo);
        var engineer = NewEngineer("Dev", Roles.Engineer);
        _engineers.Setup(e => e.ListActiveAsync(default)).ReturnsAsync(new[] { pm, pmo, engineer });
        _checkIns.Setup(c => c.GetByDateAsync(It.IsAny<DateOnly>(), It.IsAny<IReadOnlyList<Guid>>(), default))
            .ReturnsAsync(Array.Empty<CheckIn>());

        var result = await CreateHandler().Handle(new GetStandupSummaryQuery(null, null, Roles.HeadOfPmo, pmo.Id), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.MissingEngineers.Should().ContainSingle(m => m.Id == engineer.Id);
    }

    [Fact]
    public async Task ProjectManager_caller_sees_every_engineer_even_with_no_team_of_their_own()
    {
        // ProjectManager gets the same org-wide reach as HeadOfPmo/HeadOfProduct everywhere else
        // in the app (see Roles.UserManagementGlobalRoles) — a PM with no team assigned (e.g. the
        // demo's "PMO Office" persona) must not fall through to the team-lead branch and be
        // scoped down to just themselves.
        var pm = NewEngineer("Pm", Roles.ProjectManager);
        var engineerA = NewEngineer("DevA", Roles.Engineer);
        var engineerB = NewEngineer("DevB", Roles.Engineer);
        _engineers.Setup(e => e.ListActiveAsync(default)).ReturnsAsync(new[] { pm, engineerA, engineerB });
        _checkIns.Setup(c => c.GetByDateAsync(It.IsAny<DateOnly>(), It.IsAny<IReadOnlyList<Guid>>(), default))
            .ReturnsAsync(Array.Empty<CheckIn>());

        var result = await CreateHandler().Handle(new GetStandupSummaryQuery(null, null, Roles.ProjectManager, pm.Id), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.MissingEngineers.Should().Contain(m => m.Id == engineerA.Id)
            .And.Contain(m => m.Id == engineerB.Id);
    }

    [Fact]
    public async Task Entries_are_paginated_by_limit_and_cursor()
    {
        var engineerA = NewEngineer("DevA", Roles.Engineer);
        var engineerB = NewEngineer("DevB", Roles.Engineer);
        var engineerC = NewEngineer("DevC", Roles.Engineer);
        _engineers.Setup(e => e.ListActiveAsync(default)).ReturnsAsync(new[] { engineerA, engineerB, engineerC });
        var checkIns = new[]
        {
            CheckIn.Submit(engineerA.Id, DateOnly.FromDateTime(DateTime.UtcNow), "A done", "A next", null),
            CheckIn.Submit(engineerB.Id, DateOnly.FromDateTime(DateTime.UtcNow), "B done", "B next", null),
            CheckIn.Submit(engineerC.Id, DateOnly.FromDateTime(DateTime.UtcNow), "C done", "C next", null),
        };
        _checkIns.Setup(c => c.GetByDateAsync(It.IsAny<DateOnly>(), It.IsAny<IReadOnlyList<Guid>>(), default))
            .ReturnsAsync(checkIns);

        var firstPage = await CreateHandler().Handle(
            new GetStandupSummaryQuery(null, null, Roles.Executive, Guid.NewGuid(), Limit: 2), default);

        firstPage.IsSuccess.Should().BeTrue();
        firstPage.Data!.Entries.Items.Should().HaveCount(2);
        firstPage.Data.Entries.HasMore.Should().BeTrue();
        firstPage.Data.Entries.Items.Select(e => e.EngineerName).Should().Equal("DevA", "DevB");

        var secondPage = await CreateHandler().Handle(
            new GetStandupSummaryQuery(null, null, Roles.Executive, Guid.NewGuid(), Limit: 2, Cursor: firstPage.Data.Entries.NextCursor),
            default);

        secondPage.IsSuccess.Should().BeTrue();
        secondPage.Data!.Entries.Items.Should().ContainSingle(e => e.EngineerName == "DevC");
        secondPage.Data.Entries.HasMore.Should().BeFalse();
    }
}
