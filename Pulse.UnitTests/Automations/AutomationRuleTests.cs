using Pulse.Domain.Automations;
using Pulse.Domain.Common;
using FluentAssertions;

namespace Pulse.UnitTests.Automations;

public class AutomationRuleTests
{
    [Fact]
    public void Create_throws_when_name_is_blank()
    {
        var act = () => AutomationRule.Create(Guid.NewGuid(), "  ", Guid.NewGuid(), 5);

        act.Should().Throw<DomainException>().WithMessage("*Name is required*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_throws_when_threshold_is_less_than_one_day(int thresholdDays)
    {
        var act = () => AutomationRule.Create(Guid.NewGuid(), "Name", Guid.NewGuid(), thresholdDays);

        act.Should().Throw<DomainException>().WithMessage("*Threshold must be at least 1 day*");
    }

    [Fact]
    public void Create_sets_the_provided_fields_and_defaults_to_active()
    {
        var owner = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        var rule = AutomationRule.Create(owner, "  Reassign stuck tasks  ", teamId, 5);

        rule.OwnerEngineerId.Should().Be(owner);
        rule.Name.Should().Be("Reassign stuck tasks");
        rule.TeamId.Should().Be(teamId);
        rule.ThresholdDays.Should().Be(5);
        rule.IsActive.Should().BeTrue();
    }

    [Fact]
    public void UpdateDetails_updates_name_and_threshold_but_not_team()
    {
        var teamId = Guid.NewGuid();
        var rule = AutomationRule.Create(Guid.NewGuid(), "Name", teamId, 5);

        rule.UpdateDetails("Renamed", 10);

        rule.Name.Should().Be("Renamed");
        rule.ThresholdDays.Should().Be(10);
        rule.TeamId.Should().Be(teamId);
    }

    [Fact]
    public void SetActive_toggles_the_flag()
    {
        var rule = AutomationRule.Create(Guid.NewGuid(), "Name", Guid.NewGuid(), 5);

        rule.SetActive(false);

        rule.IsActive.Should().BeFalse();
    }
}
