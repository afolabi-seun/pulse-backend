using Pulse.Application.CheckIns.Queries;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.CheckIns;

public class GetCheckInStatusQueryHandlerTests
{
    private readonly Mock<ICheckInRepository> _checkIns = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITeamRepository> _teams = new();

    private GetCheckInStatusQueryHandler CreateHandler() => new(_checkIns.Object, _engineers.Object, _teams.Object);

    private static Engineer NewEngineer(string name, string role) =>
        Engineer.Create(name, $"{name.ToLower()}@test.io", "hash", role, 20, 14);

    [Fact]
    public async Task Missing_excludes_a_role_not_expected_to_check_in()
    {
        // A second, independent re-derivation of the same "who's actually expected to check in"
        // concept as GetStandupSummaryQuery's own Missing list — this handler has its own
        // scoping/missing logic entirely, so it needed its own CheckInExpected filter.
        var engineer = NewEngineer("Dev", Roles.Engineer);
        var pmo = NewEngineer("Pmo", Roles.HeadOfPmo);
        _engineers.Setup(e => e.ListActiveAsync(default)).ReturnsAsync(new[] { engineer, pmo });
        _checkIns.Setup(c => c.GetEngineersWithoutCheckInOnDateAsync(It.IsAny<DateOnly>(), default))
            .ReturnsAsync(new[] { engineer.Id, pmo.Id });

        // Caller is HeadOfPmo — org-wide scope, sees every engineer including other PMO/Exec people.
        var result = await CreateHandler().Handle(
            new GetCheckInStatusQuery(DateOnly.FromDateTime(DateTime.UtcNow), Roles.HeadOfPmo, pmo.Id), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.MissingEngineers.Should().ContainSingle(id => id == engineer.Id);
        result.Data.MissingEngineers.Should().NotContain(pmo.Id);
    }

    [Fact]
    public async Task Missing_still_includes_a_delivery_role_without_a_checkin()
    {
        var engineer = NewEngineer("Dev", Roles.Engineer);
        _engineers.Setup(e => e.ListActiveAsync(default)).ReturnsAsync(new[] { engineer });
        _checkIns.Setup(c => c.GetEngineersWithoutCheckInOnDateAsync(It.IsAny<DateOnly>(), default))
            .ReturnsAsync(new[] { engineer.Id });

        var result = await CreateHandler().Handle(
            new GetCheckInStatusQuery(DateOnly.FromDateTime(DateTime.UtcNow), Roles.HeadOfPmo, Guid.NewGuid()), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.MissingEngineers.Should().ContainSingle(id => id == engineer.Id);
        result.Data.CheckedIn.Should().BeFalse();
    }

    [Fact]
    public async Task CheckedIn_is_true_when_the_only_missing_ids_are_not_check_in_expected()
    {
        var accountant = NewEngineer("Acct", Roles.Accountant);
        _engineers.Setup(e => e.ListActiveAsync(default)).ReturnsAsync(new[] { accountant });
        _checkIns.Setup(c => c.GetEngineersWithoutCheckInOnDateAsync(It.IsAny<DateOnly>(), default))
            .ReturnsAsync(new[] { accountant.Id });

        var result = await CreateHandler().Handle(
            new GetCheckInStatusQuery(DateOnly.FromDateTime(DateTime.UtcNow), Roles.HeadOfPmo, Guid.NewGuid()), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.MissingEngineers.Should().BeEmpty();
        result.Data.CheckedIn.Should().BeTrue();
    }
}
