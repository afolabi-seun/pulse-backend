using System.Text.Json;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Notifications;
using Pulse.Domain.Alerts;
using Pulse.Domain.Notifications;

namespace Pulse.Application.Alerts;

/// <summary>Scheduled evaluation of every active AlertRule. Deliberately the only thing that decides
/// whether a rule has "fired" — AlertMetricsProvider only reports numbers, this class is the sole
/// place that compares a number to a threshold and reacts. Firing only ever sends a notification;
/// it never mutates a task, team, or project.</summary>
public class AlertRuleScanner : IRecurringJob
{
    private readonly IAlertRuleRepository _rules;
    private readonly IAlertMetricsProvider _metrics;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;
    private readonly IProjectRepository _projects;
    private readonly INotificationDispatcher _notify;
    private readonly IWebhookNotifier _webhookNotifier;
    private readonly IAlertExplainer _explainer;
    private readonly ISlackClient _slackClient;
    private readonly IAlertConversationRepository _conversations;
    private readonly IGoogleChatMessenger _googleChatMessenger;
    private readonly IGoogleChatThreadRepository _googleChatThreads;

    public AlertRuleScanner(
        IAlertRuleRepository rules,
        IAlertMetricsProvider metrics,
        IEngineerRepository engineers,
        ITeamRepository teams,
        IProjectRepository projects,
        INotificationDispatcher notify,
        IWebhookNotifier webhookNotifier,
        IAlertExplainer explainer,
        ISlackClient slackClient,
        IAlertConversationRepository conversations,
        IGoogleChatMessenger googleChatMessenger,
        IGoogleChatThreadRepository googleChatThreads)
    {
        _rules = rules;
        _metrics = metrics;
        _engineers = engineers;
        _teams = teams;
        _projects = projects;
        _notify = notify;
        _webhookNotifier = webhookNotifier;
        _explainer = explainer;
        _slackClient = slackClient;
        _conversations = conversations;
        _googleChatMessenger = googleChatMessenger;
        _googleChatThreads = googleChatThreads;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var active = await _rules.ListActiveAsync(ct);
        var now = DateTime.UtcNow;

        // Grouped so ten rules watching the same team's velocity only compute it once.
        foreach (var group in active.GroupBy(r => (r.Metric, r.ScopeType, r.ScopeId)))
        {
            var (metric, scopeType, scopeId) = group.Key;
            var value = await _metrics.GetCurrentValueAsync(metric, scopeType, scopeId, ct);

            foreach (var rule in group)
            {
                if (rule.IsInCooldown(now)) continue;
                if (!rule.IsBreached(value)) continue;

                rule.RecordTrigger(now);
                await FireAsync(rule, value, ct);
            }
        }

        await _rules.SaveChangesAsync(ct);
    }

    private async Task FireAsync(AlertRule rule, double value, CancellationToken ct)
    {
        var owner = await _engineers.GetByIdAsync(rule.OwnerEngineerId, ct);
        if (owner is null) return;

        var scopeName = rule.ScopeType == AlertScopeType.Team
            ? (await _teams.GetByIdAsync(rule.ScopeId, ct))?.Name ?? "Unknown team"
            : (await _projects.GetByIdAsync(rule.ScopeId, ct))?.Name ?? "Unknown project";

        var payload = JsonSerializer.Serialize(new
        {
            alertRuleId = rule.Id,
            metric = rule.Metric.ToString(),
            scopeName,
            value,
            threshold = rule.Threshold,
        });

        if (rule.DeliverInApp)
        {
            await _notify.NotifyAsync(owner.Id, NotificationKind.AlertRuleTriggered, payload, ct: ct);
        }

        // Drafted once and reused for both email and webhook — never blocks firing: null (no API
        // key configured, or the call failed) falls back to the same plain templated sentence this
        // used before the explainer existed.
        var plainSentence = $"{scopeName}'s {AlertMetricFormatting.MetricLabel(rule.Metric)} is {AlertMetricFormatting.FormatValue(rule.Metric, value)}, "
            + $"which is {ComparatorLabel(rule.Comparator)} your threshold of {AlertMetricFormatting.FormatValue(rule.Metric, rule.Threshold)}.";
        var explanation = (rule.DeliverEmail || rule.DeliverWebhook)
            ? await _explainer.ExplainAsync(rule, value, scopeName, ct)
            : null;

        if (rule.DeliverEmail)
        {
            var body = $"""
                <p>Hi {owner.Name},</p>
                <p>Your alert "<strong>{rule.Name}</strong>" was triggered.</p>
                <p>{explanation ?? plainSentence}</p>
                {EmailTemplate.Muted("This alert was configured by you in Pulse's My Alerts page.")}
                """;
            await _notify.EmailAsync(owner.Id, NotificationKind.AlertRuleTriggered, new NotificationEmail(owner.Email, $"Pulse alert: {rule.Name}", EmailTemplate.Layout(body)), ct);
        }

        if (rule.DeliverWebhook)
        {
            var text = explanation ?? plainSentence;

            // A Slack channel (not just a webhook URL) lets this go through chat.postMessage
            // instead of a one-way incoming webhook — the only way to get back a thread ts a
            // follow-up reply can be matched against. Falls through to Google Chat, then to the
            // plain webhook, when there's no channel configured or bot-token posting fails.
            var posted = rule.SlackChannel is not null
                ? await _slackClient.PostMessageAsync(rule.SlackChannel, $"*Pulse alert: {rule.Name}*\n{text}", ct: ct)
                : null;

            if (posted is { } result)
            {
                await _conversations.AddAsync(AlertConversation.Create(rule.Id, result.ChannelId, result.Ts), ct);
                await _conversations.SaveChangesAsync(ct);
                return;
            }

            // Same fallback shape as Slack, via the Chat REST API instead of chat.postMessage —
            // returns the thread's resource name (Chat's equivalent of Slack's ts) to thread a
            // later reply against.
            var threadName = rule.GoogleChatSpaceId is not null
                ? await _googleChatMessenger.PostToSpaceAsync(rule.GoogleChatSpaceId, $"Pulse alert: {rule.Name}\n{text}", ct: ct)
                : null;

            if (threadName is not null)
            {
                await _googleChatThreads.AddAsync(GoogleChatThread.Create(rule.Id, rule.GoogleChatSpaceId!, threadName), ct);
                await _googleChatThreads.SaveChangesAsync(ct);
            }
            else if (rule.WebhookUrl is not null)
            {
                _webhookNotifier.Enqueue(rule.WebhookUrl, $"Pulse alert: {rule.Name}", text);
            }
        }
    }

    private static string ComparatorLabel(AlertComparator comparator) => comparator switch
    {
        AlertComparator.GreaterThan => "above",
        AlertComparator.LessThan => "below",
        _ => comparator.ToString(),
    };
}
