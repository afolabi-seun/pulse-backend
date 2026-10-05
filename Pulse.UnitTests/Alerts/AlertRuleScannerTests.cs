using Pulse.Application.Alerts;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Alerts;
using Pulse.Domain.Engineers;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Alerts;

public class AlertRuleScannerTests
{
    private readonly Mock<IAlertRuleRepository> _rules = new();
    private readonly Mock<IAlertMetricsProvider> _metrics = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<IProjectRepository> _projects = new();
    private readonly Mock<INotificationRepository> _notifications = new();
    private readonly Mock<IRealtimeNotifier> _realtime = new();
    private readonly Mock<IEmailQueue> _emailQueue = new();
    private readonly Mock<IWebhookNotifier> _webhookNotifier = new();
    private readonly Mock<IAlertExplainer> _explainer = new();
    private readonly Mock<ISlackClient> _slackClient = new();
    private readonly Mock<IAlertConversationRepository> _conversations = new();
    private readonly Mock<IGoogleChatMessenger> _googleChatMessenger = new();
    private readonly Mock<IGoogleChatThreadRepository> _googleChatThreads = new();

    public AlertRuleScannerTests()
    {
        // Loose mock default (no configured API key) — exercises the plain-templated-message
        // fallback path, same as this scanner behaved before the explainer existed.
        _explainer.Setup(e => e.ExplainAsync(It.IsAny<AlertRule>(), It.IsAny<double>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        // Loose mock default (no bot token configured) — exercises the plain-incoming-webhook
        // fallback path, same as this scanner behaved before Slack thread tracking existed.
        _slackClient
            .Setup(s => s.PostMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((string, string)?)null);
        // Loose mock default (no service account configured) — exercises the same
        // plain-incoming-webhook fallback path, for the Google Chat side of that same fallback chain.
        _googleChatMessenger
            .Setup(g => g.PostToSpaceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
    }

    private AlertRuleScanner CreateScanner() =>
        new(_rules.Object, _metrics.Object, _engineers.Object, _teams.Object, _projects.Object,
            _notifications.Object, _realtime.Object, _emailQueue.Object, _webhookNotifier.Object, _explainer.Object,
            _slackClient.Object, _conversations.Object, _googleChatMessenger.Object, _googleChatThreads.Object);

    private static AlertRule NewRule(Guid owner, AlertComparator comparator = AlertComparator.GreaterThan, double threshold = 5,
        bool deliverInApp = true, bool deliverEmail = false) =>
        AlertRule.Create(owner, "Watch it", AlertMetric.BlockerCount, AlertScopeType.Team, Guid.NewGuid(),
            comparator, threshold, deliverInApp, deliverEmail);

    [Fact]
    public async Task Does_not_fire_when_the_metric_has_not_breached_the_threshold()
    {
        var rule = NewRule(Guid.NewGuid(), threshold: 5);
        _rules.Setup(r => r.ListActiveAsync(default)).ReturnsAsync(new[] { rule });
        _metrics.Setup(m => m.GetCurrentValueAsync(rule.Metric, rule.ScopeType, rule.ScopeId, default)).ReturnsAsync(3);

        await CreateScanner().RunAsync();

        rule.LastTriggeredAt.Should().BeNull();
        _notifications.Verify(n => n.AddAsync(It.IsAny<Domain.Notifications.Notification>(), default), Times.Never);
    }

    [Fact]
    public async Task Fires_an_in_app_notification_when_the_threshold_is_breached()
    {
        var owner = Engineer.Create("Owner", "owner@test.io", "hash", Roles.TeamLead, 20, 14);
        var rule = NewRule(owner.Id, threshold: 5, deliverInApp: true, deliverEmail: false);
        _rules.Setup(r => r.ListActiveAsync(default)).ReturnsAsync(new[] { rule });
        _metrics.Setup(m => m.GetCurrentValueAsync(rule.Metric, rule.ScopeType, rule.ScopeId, default)).ReturnsAsync(9);
        _engineers.Setup(e => e.GetByIdAsync(owner.Id, default)).ReturnsAsync(owner);
        _teams.Setup(t => t.GetByIdAsync(rule.ScopeId, default)).ReturnsAsync(Domain.Teams.Team.Create("Team", owner.Id, "Engineering"));

        await CreateScanner().RunAsync();

        rule.LastTriggeredAt.Should().NotBeNull();
        _notifications.Verify(n => n.AddAsync(It.IsAny<Domain.Notifications.Notification>(), default), Times.Once);
        _emailQueue.Verify(e => e.Enqueue(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Sends_email_only_when_the_rule_has_email_delivery_enabled()
    {
        var owner = Engineer.Create("Owner", "owner2@test.io", "hash", Roles.TeamLead, 20, 14);
        var rule = NewRule(owner.Id, threshold: 5, deliverInApp: false, deliverEmail: true);
        _rules.Setup(r => r.ListActiveAsync(default)).ReturnsAsync(new[] { rule });
        _metrics.Setup(m => m.GetCurrentValueAsync(rule.Metric, rule.ScopeType, rule.ScopeId, default)).ReturnsAsync(9);
        _engineers.Setup(e => e.GetByIdAsync(owner.Id, default)).ReturnsAsync(owner);
        _teams.Setup(t => t.GetByIdAsync(rule.ScopeId, default)).ReturnsAsync(Domain.Teams.Team.Create("Team", owner.Id, "Engineering"));

        await CreateScanner().RunAsync();

        _notifications.Verify(n => n.AddAsync(It.IsAny<Domain.Notifications.Notification>(), default), Times.Never);
        _emailQueue.Verify(e => e.Enqueue(owner.Email, It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Sends_a_webhook_only_when_the_rule_has_webhook_delivery_enabled()
    {
        var owner = Engineer.Create("Owner", "owner4@test.io", "hash", Roles.TeamLead, 20, 14);
        var rule = AlertRule.Create(owner.Id, "Watch it", AlertMetric.BlockerCount, AlertScopeType.Team, Guid.NewGuid(),
            AlertComparator.GreaterThan, 5, deliverInApp: false, deliverEmail: false,
            deliverWebhook: true, webhookUrl: "https://hooks.slack.com/services/x");
        _rules.Setup(r => r.ListActiveAsync(default)).ReturnsAsync(new[] { rule });
        _metrics.Setup(m => m.GetCurrentValueAsync(rule.Metric, rule.ScopeType, rule.ScopeId, default)).ReturnsAsync(9);
        _engineers.Setup(e => e.GetByIdAsync(owner.Id, default)).ReturnsAsync(owner);
        _teams.Setup(t => t.GetByIdAsync(rule.ScopeId, default)).ReturnsAsync(Domain.Teams.Team.Create("Team", owner.Id, "Engineering"));

        await CreateScanner().RunAsync();

        _webhookNotifier.Verify(w => w.Enqueue("https://hooks.slack.com/services/x", It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        _notifications.Verify(n => n.AddAsync(It.IsAny<Domain.Notifications.Notification>(), default), Times.Never);
        _emailQueue.Verify(e => e.Enqueue(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Posts_to_Google_Chat_instead_of_the_plain_webhook_when_a_space_is_configured()
    {
        var owner = Engineer.Create("Owner", "owner5@test.io", "hash", Roles.TeamLead, 20, 14);
        var rule = AlertRule.Create(owner.Id, "Watch it", AlertMetric.BlockerCount, AlertScopeType.Team, Guid.NewGuid(),
            AlertComparator.GreaterThan, 5, deliverInApp: false, deliverEmail: false,
            deliverWebhook: true, webhookUrl: "https://example.org/webhook", googleChatSpaceId: "spaces/AAAAAAAAAAA");
        _rules.Setup(r => r.ListActiveAsync(default)).ReturnsAsync(new[] { rule });
        _metrics.Setup(m => m.GetCurrentValueAsync(rule.Metric, rule.ScopeType, rule.ScopeId, default)).ReturnsAsync(9);
        _engineers.Setup(e => e.GetByIdAsync(owner.Id, default)).ReturnsAsync(owner);
        _teams.Setup(t => t.GetByIdAsync(rule.ScopeId, default)).ReturnsAsync(Domain.Teams.Team.Create("Team", owner.Id, "Engineering"));
        _googleChatMessenger
            .Setup(g => g.PostToSpaceAsync("spaces/AAAAAAAAAAA", It.IsAny<string>(), null, default))
            .ReturnsAsync("spaces/AAAAAAAAAAA/threads/BBBB");

        await CreateScanner().RunAsync();

        _googleChatThreads.Verify(t => t.AddAsync(
            It.Is<Domain.Alerts.GoogleChatThread>(th => th.AlertRuleId == rule.Id && th.SpaceId == "spaces/AAAAAAAAAAA" && th.ThreadName == "spaces/AAAAAAAAAAA/threads/BBBB"),
            default), Times.Once);
        _webhookNotifier.Verify(w => w.Enqueue(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Does_not_refire_while_the_rule_is_in_its_cooldown_window()
    {
        var owner = Engineer.Create("Owner", "owner3@test.io", "hash", Roles.TeamLead, 20, 14);
        var rule = NewRule(owner.Id, threshold: 5);
        rule.RecordTrigger(DateTime.UtcNow.AddHours(-1)); // fired an hour ago, well inside the 24h cooldown
        _rules.Setup(r => r.ListActiveAsync(default)).ReturnsAsync(new[] { rule });
        _metrics.Setup(m => m.GetCurrentValueAsync(rule.Metric, rule.ScopeType, rule.ScopeId, default)).ReturnsAsync(9);

        await CreateScanner().RunAsync();

        _notifications.Verify(n => n.AddAsync(It.IsAny<Domain.Notifications.Notification>(), default), Times.Never);
    }

    [Fact]
    public async Task Computes_the_metric_once_for_multiple_rules_watching_the_same_scope()
    {
        var scopeId = Guid.NewGuid();
        var ownerA = Engineer.Create("A", "a@test.io", "hash", Roles.TeamLead, 20, 14);
        var ownerB = Engineer.Create("B", "b@test.io", "hash", Roles.TeamLead, 20, 14);
        var ruleA = AlertRule.Create(ownerA.Id, "A's rule", AlertMetric.BlockerCount, AlertScopeType.Team, scopeId, AlertComparator.GreaterThan, 5, true, false);
        var ruleB = AlertRule.Create(ownerB.Id, "B's rule", AlertMetric.BlockerCount, AlertScopeType.Team, scopeId, AlertComparator.GreaterThan, 8, true, false);
        _rules.Setup(r => r.ListActiveAsync(default)).ReturnsAsync(new[] { ruleA, ruleB });
        _metrics.Setup(m => m.GetCurrentValueAsync(AlertMetric.BlockerCount, AlertScopeType.Team, scopeId, default)).ReturnsAsync(9);
        _engineers.Setup(e => e.GetByIdAsync(ownerA.Id, default)).ReturnsAsync(ownerA);
        _engineers.Setup(e => e.GetByIdAsync(ownerB.Id, default)).ReturnsAsync(ownerB);
        _teams.Setup(t => t.GetByIdAsync(scopeId, default)).ReturnsAsync(Domain.Teams.Team.Create("Team", ownerA.Id, "Engineering"));

        await CreateScanner().RunAsync();

        _metrics.Verify(m => m.GetCurrentValueAsync(AlertMetric.BlockerCount, AlertScopeType.Team, scopeId, default), Times.Once);
        ruleA.LastTriggeredAt.Should().NotBeNull();
        ruleB.LastTriggeredAt.Should().NotBeNull();
    }
}
