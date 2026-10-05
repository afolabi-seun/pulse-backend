using Pulse.Domain.Alerts;
using Pulse.Domain.Common;
using FluentAssertions;

namespace Pulse.UnitTests.Alerts;

public class AlertRuleTests
{
    private static AlertRule NewTeamRule(AlertMetric metric = AlertMetric.BlockerCount, AlertComparator comparator = AlertComparator.GreaterThan, double threshold = 5) =>
        AlertRule.Create(Guid.NewGuid(), "Too many blockers", metric, AlertScopeType.Team, Guid.NewGuid(), comparator, threshold, deliverInApp: true, deliverEmail: false);

    [Fact]
    public void Create_throws_when_name_is_blank()
    {
        var act = () => AlertRule.Create(Guid.NewGuid(), "  ", AlertMetric.BlockerCount, AlertScopeType.Team, Guid.NewGuid(), AlertComparator.GreaterThan, 5, true, false);

        act.Should().Throw<DomainException>().WithMessage("*Name is required*");
    }

    [Fact]
    public void Create_throws_when_no_delivery_channel_is_selected()
    {
        var act = () => AlertRule.Create(Guid.NewGuid(), "Name", AlertMetric.BlockerCount, AlertScopeType.Team, Guid.NewGuid(), AlertComparator.GreaterThan, 5, false, false);

        act.Should().Throw<DomainException>().WithMessage("*delivery channel*");
    }

    [Theory]
    [InlineData(AlertMetric.TeamVelocity, AlertScopeType.Project)]
    [InlineData(AlertMetric.CheckInCompliance, AlertScopeType.Project)]
    [InlineData(AlertMetric.QaRejectRate, AlertScopeType.Team)]
    [InlineData(AlertMetric.TeamVelocityChange, AlertScopeType.Project)]
    [InlineData(AlertMetric.CheckInComplianceChange, AlertScopeType.Project)]
    [InlineData(AlertMetric.QaRejectRateChange, AlertScopeType.Team)]
    public void Create_throws_when_metric_is_scoped_to_the_wrong_type(AlertMetric metric, AlertScopeType scopeType)
    {
        var act = () => AlertRule.Create(Guid.NewGuid(), "Name", metric, scopeType, Guid.NewGuid(), AlertComparator.GreaterThan, 5, true, false);

        act.Should().Throw<DomainException>().WithMessage("*cannot be scoped to*");
    }

    [Fact]
    public void BlockerCount_can_be_scoped_to_either_Team_or_Project()
    {
        var teamRule = AlertRule.Create(Guid.NewGuid(), "N", AlertMetric.BlockerCount, AlertScopeType.Team, Guid.NewGuid(), AlertComparator.GreaterThan, 5, true, false);
        var projectRule = AlertRule.Create(Guid.NewGuid(), "N", AlertMetric.BlockerCount, AlertScopeType.Project, Guid.NewGuid(), AlertComparator.GreaterThan, 5, true, false);

        teamRule.ScopeType.Should().Be(AlertScopeType.Team);
        projectRule.ScopeType.Should().Be(AlertScopeType.Project);
    }

    [Fact]
    public void BlockerCountChange_can_be_scoped_to_either_Team_or_Project()
    {
        var teamRule = AlertRule.Create(Guid.NewGuid(), "N", AlertMetric.BlockerCountChange, AlertScopeType.Team, Guid.NewGuid(), AlertComparator.GreaterThan, 5, true, false);
        var projectRule = AlertRule.Create(Guid.NewGuid(), "N", AlertMetric.BlockerCountChange, AlertScopeType.Project, Guid.NewGuid(), AlertComparator.GreaterThan, 5, true, false);

        teamRule.ScopeType.Should().Be(AlertScopeType.Team);
        projectRule.ScopeType.Should().Be(AlertScopeType.Project);
    }

    [Theory]
    [InlineData(AlertComparator.GreaterThan, 6, 5, true)]
    [InlineData(AlertComparator.GreaterThan, 5, 5, false)]
    [InlineData(AlertComparator.LessThan, 4, 5, true)]
    [InlineData(AlertComparator.LessThan, 5, 5, false)]
    public void IsBreached_compares_the_current_value_against_the_threshold(AlertComparator comparator, double current, double threshold, bool expected)
    {
        var rule = NewTeamRule(comparator: comparator, threshold: threshold);

        rule.IsBreached(current).Should().Be(expected);
    }

    [Fact]
    public void IsInCooldown_is_false_before_the_rule_has_ever_fired()
    {
        var rule = NewTeamRule();

        rule.IsInCooldown(DateTime.UtcNow).Should().BeFalse();
    }

    [Fact]
    public void IsInCooldown_is_true_immediately_after_firing()
    {
        var rule = NewTeamRule();
        var firedAt = DateTime.UtcNow;
        rule.RecordTrigger(firedAt);

        rule.IsInCooldown(firedAt.AddHours(1)).Should().BeTrue();
    }

    [Fact]
    public void IsInCooldown_is_false_after_the_cooldown_window_elapses()
    {
        var rule = NewTeamRule();
        var firedAt = DateTime.UtcNow;
        rule.RecordTrigger(firedAt);

        rule.IsInCooldown(firedAt.AddHours(25)).Should().BeFalse();
    }

    [Fact]
    public void UpdateDetails_changes_name_threshold_comparator_and_delivery_but_not_metric_or_scope()
    {
        var rule = NewTeamRule();
        var originalScopeId = rule.ScopeId;

        rule.UpdateDetails("Renamed", AlertComparator.LessThan, 2, deliverInApp: false, deliverEmail: true);

        rule.Name.Should().Be("Renamed");
        rule.Comparator.Should().Be(AlertComparator.LessThan);
        rule.Threshold.Should().Be(2);
        rule.DeliverInApp.Should().BeFalse();
        rule.DeliverEmail.Should().BeTrue();
        rule.Metric.Should().Be(AlertMetric.BlockerCount);
        rule.ScopeId.Should().Be(originalScopeId);
    }

    [Fact]
    public void SetActive_toggles_the_flag()
    {
        var rule = NewTeamRule();

        rule.SetActive(false);
        rule.IsActive.Should().BeFalse();

        rule.SetActive(true);
        rule.IsActive.Should().BeTrue();
    }

    // ── Webhook delivery ──────────────────────────────────────────────────────

    [Fact]
    public void Create_throws_when_webhook_delivery_is_enabled_with_no_url()
    {
        var act = () => AlertRule.Create(Guid.NewGuid(), "Name", AlertMetric.BlockerCount, AlertScopeType.Team, Guid.NewGuid(),
            AlertComparator.GreaterThan, 5, deliverInApp: false, deliverEmail: false, deliverWebhook: true, webhookUrl: null);

        act.Should().Throw<DomainException>().WithMessage("*webhook URL is required*");
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("http://hooks.slack.com/services/x")]
    [InlineData("ftp://hooks.slack.com/services/x")]
    public void Create_throws_when_the_webhook_url_is_not_a_valid_https_url(string url)
    {
        var act = () => AlertRule.Create(Guid.NewGuid(), "Name", AlertMetric.BlockerCount, AlertScopeType.Team, Guid.NewGuid(),
            AlertComparator.GreaterThan, 5, deliverInApp: false, deliverEmail: false, deliverWebhook: true, webhookUrl: url);

        act.Should().Throw<DomainException>().WithMessage("*valid https URL*");
    }

    [Fact]
    public void Create_succeeds_with_webhook_delivery_and_a_valid_https_url()
    {
        var rule = AlertRule.Create(Guid.NewGuid(), "Name", AlertMetric.BlockerCount, AlertScopeType.Team, Guid.NewGuid(),
            AlertComparator.GreaterThan, 5, deliverInApp: false, deliverEmail: false,
            deliverWebhook: true, webhookUrl: "https://hooks.slack.com/services/x");

        rule.DeliverWebhook.Should().BeTrue();
        rule.WebhookUrl.Should().Be("https://hooks.slack.com/services/x");
    }

    [Fact]
    public void Create_clears_the_webhook_url_when_webhook_delivery_is_off()
    {
        var rule = AlertRule.Create(Guid.NewGuid(), "Name", AlertMetric.BlockerCount, AlertScopeType.Team, Guid.NewGuid(),
            AlertComparator.GreaterThan, 5, deliverInApp: true, deliverEmail: false,
            deliverWebhook: false, webhookUrl: "https://hooks.slack.com/services/x");

        rule.DeliverWebhook.Should().BeFalse();
        rule.WebhookUrl.Should().BeNull();
    }

    [Fact]
    public void UpdateDetails_can_turn_on_webhook_delivery_with_a_url()
    {
        var rule = NewTeamRule();

        rule.UpdateDetails("Name", AlertComparator.GreaterThan, 5, deliverInApp: true, deliverEmail: false,
            deliverWebhook: true, webhookUrl: "https://outlook.office.com/webhook/x");

        rule.DeliverWebhook.Should().BeTrue();
        rule.WebhookUrl.Should().Be("https://outlook.office.com/webhook/x");
    }

    [Fact]
    public void UpdateDetails_throws_when_turning_on_webhook_delivery_without_a_url()
    {
        var rule = NewTeamRule();

        var act = () => rule.UpdateDetails("Name", AlertComparator.GreaterThan, 5, deliverInApp: true, deliverEmail: false,
            deliverWebhook: true, webhookUrl: null);

        act.Should().Throw<DomainException>().WithMessage("*webhook URL is required*");
    }
}
