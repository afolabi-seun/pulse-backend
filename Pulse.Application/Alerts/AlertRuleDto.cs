using Pulse.Domain.Alerts;

namespace Pulse.Application.Alerts;

public record AlertRuleDto(
    Guid Id,
    string Name,
    string Metric,
    string ScopeType,
    Guid ScopeId,
    string? ScopeName,
    string Comparator,
    double Threshold,
    bool DeliverInApp,
    bool DeliverEmail,
    bool DeliverWebhook,
    string? WebhookUrl,
    string? SlackChannel,
    string? GoogleChatSpaceId,
    bool IsActive,
    DateTime? LastTriggeredAt,
    DateTime CreatedAt)
{
    public static AlertRuleDto From(AlertRule r, string? scopeName = null) => new(
        r.Id, r.Name, Camel(r.Metric.ToString()), Camel(r.ScopeType.ToString()), r.ScopeId, scopeName,
        Camel(r.Comparator.ToString()), r.Threshold, r.DeliverInApp, r.DeliverEmail, r.DeliverWebhook, r.WebhookUrl,
        r.SlackChannel, r.GoogleChatSpaceId, r.IsActive, r.LastTriggeredAt, r.CreatedAt);

    private static string Camel(string s) => s.Length == 0 ? s : char.ToLower(s[0]) + s[1..];
}
