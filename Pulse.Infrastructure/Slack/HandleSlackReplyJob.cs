using Pulse.Application.Common.Interfaces;

namespace Pulse.Infrastructure.Slack;

/// <summary>Hangfire job that answers a threaded reply to an alert Pulse posted to Slack. Runs
/// entirely in the background — the controller that enqueues this has already returned 200 to
/// Slack, since Slack's own ~3s timeout leaves no room for an LLM round trip in the request path.
/// A no-op (not an error) whenever the thread isn't one Pulse started, or the explainer can't
/// produce an answer — this must never throw back into Hangfire for those ordinary cases.</summary>
public class HandleSlackReplyJob
{
    private readonly IAlertConversationRepository _conversations;
    private readonly IAlertRuleRepository _rules;
    private readonly IAlertExplainer _explainer;
    private readonly ISlackClient _slack;

    public HandleSlackReplyJob(
        IAlertConversationRepository conversations,
        IAlertRuleRepository rules,
        IAlertExplainer explainer,
        ISlackClient slack)
    {
        _conversations = conversations;
        _rules = rules;
        _explainer = explainer;
        _slack = slack;
    }

    public async Task ExecuteAsync(string channel, string threadTs, string question)
    {
        var conversation = await _conversations.FindAsync(channel, threadTs);
        if (conversation is null) return; // not a thread Pulse started — ignore

        var rule = await _rules.GetByIdAsync(conversation.AlertRuleId);
        if (rule is null) return; // rule was deleted since the alert fired

        var answer = await _explainer.AnswerFollowUpAsync(rule, question);
        if (answer is null) return; // no API key configured, or the call failed — stay silent rather than post a canned "I don't know"

        await _slack.PostMessageAsync(channel, answer, threadTs);
    }
}
