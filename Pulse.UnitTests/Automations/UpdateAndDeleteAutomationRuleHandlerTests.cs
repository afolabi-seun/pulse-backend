using Pulse.Application.Automations.Commands;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Automations;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Automations;

public class UpdateAutomationRuleHandlerTests
{
    private readonly Mock<IAutomationRuleRepository> _rules = new();

    private UpdateAutomationRuleHandler CreateHandler() => new(_rules.Object);

    private static AutomationRule NewRule(Guid owner) =>
        AutomationRule.Create(owner, "Original", Guid.NewGuid(), 5);

    [Fact]
    public async Task Returns_NOT_FOUND_when_the_rule_does_not_exist()
    {
        _rules.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((AutomationRule?)null);

        var result = await CreateHandler().Handle(
            new UpdateAutomationRuleCommand(Guid.NewGuid(), "New", 3, null, Guid.NewGuid()), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task Returns_FORBIDDEN_when_the_actor_does_not_own_the_rule()
    {
        var rule = NewRule(Guid.NewGuid());
        _rules.Setup(r => r.GetByIdAsync(rule.Id, default)).ReturnsAsync(rule);

        var result = await CreateHandler().Handle(
            new UpdateAutomationRuleCommand(rule.Id, "New", 3, null, Guid.NewGuid()), default);

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
            new UpdateAutomationRuleCommand(rule.Id, "Renamed", 10, false, owner), default);

        result.IsSuccess.Should().BeTrue();
        rule.Name.Should().Be("Renamed");
        rule.ThresholdDays.Should().Be(10);
        rule.IsActive.Should().BeFalse();
        _rules.Verify(r => r.SaveChangesAsync(default), Times.Once);
    }
}

public class DeleteAutomationRuleHandlerTests
{
    private readonly Mock<IAutomationRuleRepository> _rules = new();

    private DeleteAutomationRuleHandler CreateHandler() => new(_rules.Object);

    private static AutomationRule NewRule(Guid owner) =>
        AutomationRule.Create(owner, "Original", Guid.NewGuid(), 5);

    [Fact]
    public async Task Returns_FORBIDDEN_when_the_actor_does_not_own_the_rule()
    {
        var rule = NewRule(Guid.NewGuid());
        _rules.Setup(r => r.GetByIdAsync(rule.Id, default)).ReturnsAsync(rule);

        var result = await CreateHandler().Handle(new DeleteAutomationRuleCommand(rule.Id, Guid.NewGuid()), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
        _rules.Verify(r => r.DeleteAsync(It.IsAny<AutomationRule>(), default), Times.Never);
    }

    [Fact]
    public async Task Deletes_the_rule_when_the_owner_requests_it()
    {
        var owner = Guid.NewGuid();
        var rule = NewRule(owner);
        _rules.Setup(r => r.GetByIdAsync(rule.Id, default)).ReturnsAsync(rule);

        var result = await CreateHandler().Handle(new DeleteAutomationRuleCommand(rule.Id, owner), default);

        result.IsSuccess.Should().BeTrue();
        _rules.Verify(r => r.DeleteAsync(rule, default), Times.Once);
    }
}
