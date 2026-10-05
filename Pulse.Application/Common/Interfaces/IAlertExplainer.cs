using Pulse.Domain.Alerts;

namespace Pulse.Application.Common.Interfaces;

/// <summary>Drafts a plain-language explanation for a fired AlertRule, using read-only tools scoped
/// to the rule's own team/project — it explains why a threshold was crossed, it never decides
/// whether one was (AlertRuleScanner already decided that) and never mutates anything. Returns null
/// when it can't produce one (no API key configured, or the call failed for any reason) — callers
/// fall back to their own plain templated message in that case; this must never throw.</summary>
public interface IAlertExplainer
{
    Task<string?> ExplainAsync(AlertRule rule, double value, string scopeName, CancellationToken ct = default);

    /// <summary>Continues an existing explanation as a follow-up question in the same scoped
    /// context — backs Slack thread replies. Returns null on the same terms as ExplainAsync.</summary>
    Task<string?> AnswerFollowUpAsync(AlertRule rule, string followUpQuestion, CancellationToken ct = default);
}
