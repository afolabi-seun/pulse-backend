using Pulse.Application.Alerts.Commands;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Alerts;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Alerts;

public class UpdateAlertRuleHandlerTests
{
    private readonly Mock<IAlertRuleRepository> _rules = new();

    // No organization set: the default org, where Slack/Google Chat delivery is available.
    private readonly Mock<ICurrentUserService> _currentUser = new();

    private UpdateAlertRuleHandler CreateHandler() => new(_rules.Object, _currentUser.Object);

    private static AlertRule NewRule(Guid owner) =>
        AlertRule.Create(owner, "Original", AlertMetric.BlockerCount, AlertScopeType.Team, Guid.NewGuid(),
            AlertComparator.GreaterThan, 5, true, false);

    [Fact]
    public async Task Returns_NOT_FOUND_when_the_rule_does_not_exist()
    {
        _rules.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((AlertRule?)null);

        var result = await CreateHandler().Handle(
            new UpdateAlertRuleCommand(Guid.NewGuid(), "New", AlertComparator.LessThan, 1, true, false, null, Guid.NewGuid()), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task Returns_FORBIDDEN_when_the_actor_does_not_own_the_rule()
    {
        var rule = NewRule(Guid.NewGuid());
        _rules.Setup(r => r.GetByIdAsync(rule.Id, default)).ReturnsAsync(rule);

        var result = await CreateHandler().Handle(
            new UpdateAlertRuleCommand(rule.Id, "New", AlertComparator.LessThan, 1, true, false, null, Guid.NewGuid()), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
    }

    [Fact]
    public async Task Updates_the_rules_editable_fields_and_active_flag()
    {
        var owner = Guid.NewGuid();
        var rule = NewRule(owner);
        _rules.Setup(r => r.GetByIdAsync(rule.Id, default)).ReturnsAsync(rule);

        var result = await CreateHandler().Handle(
            new UpdateAlertRuleCommand(rule.Id, "Renamed", AlertComparator.LessThan, 2, false, true, false, owner), default);

        result.IsSuccess.Should().BeTrue();
        rule.Name.Should().Be("Renamed");
        rule.IsActive.Should().BeFalse();
        _rules.Verify(r => r.SaveChangesAsync(default), Times.Once);
    }
}

public class DeleteAlertRuleHandlerTests
{
    private readonly Mock<IAlertRuleRepository> _rules = new();

    private DeleteAlertRuleHandler CreateHandler() => new(_rules.Object);

    private static AlertRule NewRule(Guid owner) =>
        AlertRule.Create(owner, "Original", AlertMetric.BlockerCount, AlertScopeType.Team, Guid.NewGuid(),
            AlertComparator.GreaterThan, 5, true, false);

    [Fact]
    public async Task Returns_FORBIDDEN_when_the_actor_does_not_own_the_rule()
    {
        var rule = NewRule(Guid.NewGuid());
        _rules.Setup(r => r.GetByIdAsync(rule.Id, default)).ReturnsAsync(rule);

        var result = await CreateHandler().Handle(new DeleteAlertRuleCommand(rule.Id, Guid.NewGuid()), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
        _rules.Verify(r => r.DeleteAsync(It.IsAny<AlertRule>(), default), Times.Never);
    }

    [Fact]
    public async Task Deletes_the_rule_when_the_owner_requests_it()
    {
        var owner = Guid.NewGuid();
        var rule = NewRule(owner);
        _rules.Setup(r => r.GetByIdAsync(rule.Id, default)).ReturnsAsync(rule);

        var result = await CreateHandler().Handle(new DeleteAlertRuleCommand(rule.Id, owner), default);

        result.IsSuccess.Should().BeTrue();
        _rules.Verify(r => r.DeleteAsync(rule, default), Times.Once);
    }
}
