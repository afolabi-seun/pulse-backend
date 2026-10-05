using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Alerts;
using Pulse.Domain.Engineers;
using Pulse.Infrastructure.Slack;
using Moq;

namespace Pulse.UnitTests.Alerts;

public class HandleSlackReplyJobTests
{
    private readonly Mock<IAlertConversationRepository> _conversations = new();
    private readonly Mock<IAlertRuleRepository> _rules = new();
    private readonly Mock<IAlertExplainer> _explainer = new();
    private readonly Mock<ISlackClient> _slack = new();

    private HandleSlackReplyJob CreateJob() => new(_conversations.Object, _rules.Object, _explainer.Object, _slack.Object);

    [Fact]
    public async Task Does_nothing_when_the_thread_is_not_one_Pulse_started()
    {
        _conversations.Setup(c => c.FindAsync("C1", "1700.0", default)).ReturnsAsync((AlertConversation?)null);

        await CreateJob().ExecuteAsync("C1", "1700.0", "why though?");

        _rules.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), default), Times.Never);
        _slack.Verify(s => s.PostMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), default), Times.Never);
    }

    [Fact]
    public async Task Does_nothing_when_the_rule_no_longer_exists()
    {
        var conversation = AlertConversation.Create(Guid.NewGuid(), "C1", "1700.0");
        _conversations.Setup(c => c.FindAsync("C1", "1700.0", default)).ReturnsAsync(conversation);
        _rules.Setup(r => r.GetByIdAsync(conversation.AlertRuleId, default)).ReturnsAsync((AlertRule?)null);

        await CreateJob().ExecuteAsync("C1", "1700.0", "why though?");

        _explainer.Verify(e => e.AnswerFollowUpAsync(It.IsAny<AlertRule>(), It.IsAny<string>(), default), Times.Never);
    }

    [Fact]
    public async Task Stays_silent_when_the_explainer_cannot_produce_an_answer()
    {
        var owner = Engineer.Create("Owner", "owner@test.io", "hash", Roles.TeamLead, 20, 14);
        var rule = AlertRule.Create(owner.Id, "Watch it", AlertMetric.BlockerCount, AlertScopeType.Team, Guid.NewGuid(),
            AlertComparator.GreaterThan, 5, true, false);
        var conversation = AlertConversation.Create(rule.Id, "C1", "1700.0");
        _conversations.Setup(c => c.FindAsync("C1", "1700.0", default)).ReturnsAsync(conversation);
        _rules.Setup(r => r.GetByIdAsync(rule.Id, default)).ReturnsAsync(rule);
        _explainer.Setup(e => e.AnswerFollowUpAsync(rule, "why though?", default)).ReturnsAsync((string?)null);

        await CreateJob().ExecuteAsync("C1", "1700.0", "why though?");

        _slack.Verify(s => s.PostMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), default), Times.Never);
    }

    [Fact]
    public async Task Posts_the_answer_back_into_the_same_thread()
    {
        var owner = Engineer.Create("Owner", "owner2@test.io", "hash", Roles.TeamLead, 20, 14);
        var rule = AlertRule.Create(owner.Id, "Watch it", AlertMetric.BlockerCount, AlertScopeType.Team, Guid.NewGuid(),
            AlertComparator.GreaterThan, 5, true, false);
        var conversation = AlertConversation.Create(rule.Id, "C1", "1700.0");
        _conversations.Setup(c => c.FindAsync("C1", "1700.0", default)).ReturnsAsync(conversation);
        _rules.Setup(r => r.GetByIdAsync(rule.Id, default)).ReturnsAsync(rule);
        _explainer.Setup(e => e.AnswerFollowUpAsync(rule, "why though?", default)).ReturnsAsync("Because three tasks are blocked.");

        await CreateJob().ExecuteAsync("C1", "1700.0", "why though?");

        _slack.Verify(s => s.PostMessageAsync("C1", "Because three tasks are blocked.", "1700.0", default), Times.Once);
    }
}
