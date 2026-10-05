using Pulse.Domain.Common;

namespace Pulse.Domain.Alerts;

/// <summary>A user-configured watch on a metric already computed elsewhere in Pulse (blocker
/// count, team velocity, check-in compliance, QA reject rate) — see AlertMetric. Evaluated on a
/// schedule by AlertRuleScanner; firing never mutates anything else in the system, only sends a
/// notification. Deliberately narrower than a full "if X then do Y" automation: it explains a
/// threshold breach, it never acts on the caller's behalf.</summary>
public class AlertRule : Entity
{
    public Guid OwnerEngineerId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public AlertMetric Metric { get; private set; }
    public AlertScopeType ScopeType { get; private set; }
    public Guid ScopeId { get; private set; }
    public AlertComparator Comparator { get; private set; }
    public double Threshold { get; private set; }
    public bool DeliverInApp { get; private set; }
    public bool DeliverEmail { get; private set; }
    public bool DeliverWebhook { get; private set; }
    /// <summary>A Slack or Microsoft Teams incoming webhook URL — which of the two is inferred from
    /// the host at send time (see WebhookSender), so this field doesn't need a separate platform
    /// flag. Null unless DeliverWebhook is true.</summary>
    public string? WebhookUrl { get; private set; }
    /// <summary>A Slack channel ID or name (e.g. "C0123ABC" or "#team-alerts") — independent of
    /// WebhookUrl, because follow-up Q&A needs the Slack Web API's chat.postMessage (which returns a
    /// thread ts to reply into) rather than an incoming webhook (which is one-way and returns
    /// nothing usable for threading). Only takes effect when the server has a Slack bot token
    /// configured; otherwise this rule just keeps using its plain incoming webhook, one-way, exactly
    /// as before. Optional even when DeliverWebhook is true — a rule can stay one-way on purpose.</summary>
    public string? SlackChannel { get; private set; }
    /// <summary>A GoogleChatSpace.SpaceId — the Google Chat equivalent of SlackChannel, enabling
    /// threaded follow-up Q&amp;A via the Chat REST API instead of a one-way webhook. Unlike
    /// SlackChannel, this can't be arbitrary text: it must match a space the app has actually been
    /// added to (see GoogleChatSpace), so the frontend offers it as a picker, not a free-text field.
    /// Optional even when DeliverWebhook is true, same as SlackChannel.</summary>
    public string? GoogleChatSpaceId { get; private set; }
    public bool IsActive { get; private set; } = true;
    /// <summary>Null until the rule has fired at least once. Gates the cooldown window — see
    /// <see cref="IsInCooldown"/> — so a condition that stays breached across many scan cycles
    /// doesn't re-notify every 30 minutes.</summary>
    public DateTime? LastTriggeredAt { get; private set; }

    private static readonly TimeSpan CooldownWindow = TimeSpan.FromHours(24);

    private AlertRule() { }

    public static AlertRule Create(
        Guid ownerEngineerId, string name, AlertMetric metric, AlertScopeType scopeType, Guid scopeId,
        AlertComparator comparator, double threshold, bool deliverInApp, bool deliverEmail,
        bool deliverWebhook = false, string? webhookUrl = null, string? slackChannel = null, string? googleChatSpaceId = null)
    {
        ValidateName(name);
        ValidateDelivery(deliverInApp, deliverEmail, deliverWebhook, webhookUrl);
        ValidateScope(metric, scopeType);

        return new()
        {
            OwnerEngineerId = ownerEngineerId,
            Name = name.Trim(),
            Metric = metric,
            ScopeType = scopeType,
            ScopeId = scopeId,
            Comparator = comparator,
            Threshold = threshold,
            DeliverInApp = deliverInApp,
            DeliverEmail = deliverEmail,
            DeliverWebhook = deliverWebhook,
            WebhookUrl = deliverWebhook ? webhookUrl!.Trim() : null,
            SlackChannel = deliverWebhook && !string.IsNullOrWhiteSpace(slackChannel) ? slackChannel.Trim() : null,
            GoogleChatSpaceId = deliverWebhook && !string.IsNullOrWhiteSpace(googleChatSpaceId) ? googleChatSpaceId.Trim() : null,
        };
    }

    /// <summary>Metric and scope are fixed at creation — changing what a rule watches or where is
    /// close enough to "delete and recreate" that there's no real value in supporting it, and it
    /// avoids ever needing to re-validate ValidateScope against a stale ScopeId.</summary>
    public void UpdateDetails(
        string name, AlertComparator comparator, double threshold, bool deliverInApp, bool deliverEmail,
        bool deliverWebhook = false, string? webhookUrl = null, string? slackChannel = null, string? googleChatSpaceId = null)
    {
        ValidateName(name);
        ValidateDelivery(deliverInApp, deliverEmail, deliverWebhook, webhookUrl);

        Name = name.Trim();
        Comparator = comparator;
        Threshold = threshold;
        DeliverInApp = deliverInApp;
        DeliverEmail = deliverEmail;
        DeliverWebhook = deliverWebhook;
        WebhookUrl = deliverWebhook ? webhookUrl!.Trim() : null;
        SlackChannel = deliverWebhook && !string.IsNullOrWhiteSpace(slackChannel) ? slackChannel.Trim() : null;
        GoogleChatSpaceId = deliverWebhook && !string.IsNullOrWhiteSpace(googleChatSpaceId) ? googleChatSpaceId.Trim() : null;
    }

    public void SetActive(bool isActive) => IsActive = isActive;

    public bool IsInCooldown(DateTime now) =>
        LastTriggeredAt.HasValue && now - LastTriggeredAt.Value < CooldownWindow;

    public bool IsBreached(double currentValue) => Comparator switch
    {
        AlertComparator.GreaterThan => currentValue > Threshold,
        AlertComparator.LessThan => currentValue < Threshold,
        _ => false,
    };

    public void RecordTrigger(DateTime firedAt) => LastTriggeredAt = firedAt;

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Name is required.");
    }

    private static void ValidateDelivery(bool deliverInApp, bool deliverEmail, bool deliverWebhook, string? webhookUrl)
    {
        if (!deliverInApp && !deliverEmail && !deliverWebhook)
            throw new DomainException("At least one delivery channel is required.");
        if (!deliverWebhook)
            return;
        if (string.IsNullOrWhiteSpace(webhookUrl))
            throw new DomainException("A webhook URL is required when webhook delivery is enabled.");
        if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new DomainException("Webhook URL must be a valid https URL.");
    }

    private static void ValidateScope(AlertMetric metric, AlertScopeType scopeType)
    {
        var valid = metric switch
        {
            AlertMetric.BlockerCount => true, // Team or Project
            AlertMetric.TeamVelocity => scopeType == AlertScopeType.Team,
            AlertMetric.CheckInCompliance => scopeType == AlertScopeType.Team,
            AlertMetric.QaRejectRate => scopeType == AlertScopeType.Project,
            AlertMetric.TeamVelocityChange => scopeType == AlertScopeType.Team,
            AlertMetric.BlockerCountChange => true, // Team or Project, same as BlockerCount
            AlertMetric.CheckInComplianceChange => scopeType == AlertScopeType.Team,
            AlertMetric.QaRejectRateChange => scopeType == AlertScopeType.Project,
            _ => false,
        };
        if (!valid)
            throw new DomainException($"{metric} cannot be scoped to a {scopeType}.");
    }
}
