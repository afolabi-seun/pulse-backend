using Pulse.Application.Common.Interfaces;

namespace Pulse.Infrastructure.GoogleChat;

/// <summary>Google Chat's equivalent of HandleSlackReplyJob/HandleTeamsReplyJob — answers a
/// threaded reply to an alert Pulse posted into a Chat space. Also owns the "is this thread one
/// Pulse started" decision (via the FindAsync no-op below): unlike Slack's thread_ts or Teams'
/// ReplyToId, Chat's message.thread.name is populated on every message including brand-new ones, so
/// there's no cheap "is this even a reply" signal the receiving controller can filter on first —
/// every genuine user message gets enqueued here, and this lookup is what actually decides whether
/// it's ours to answer.</summary>
public class HandleGoogleChatReplyJob
{
    private readonly IGoogleChatThreadRepository _threads;
    private readonly IAlertRuleRepository _rules;
    private readonly IAlertExplainer _explainer;
    private readonly IGoogleChatMessenger _chat;

    public HandleGoogleChatReplyJob(
        IGoogleChatThreadRepository threads,
        IAlertRuleRepository rules,
        IAlertExplainer explainer,
        IGoogleChatMessenger chat)
    {
        _threads = threads;
        _rules = rules;
        _explainer = explainer;
        _chat = chat;
    }

    public async Task ExecuteAsync(string spaceId, string threadName, string question)
    {
        var thread = await _threads.FindAsync(spaceId, threadName);
        if (thread is null) return; // not a thread Pulse started — ignore

        var rule = await _rules.GetByIdAsync(thread.AlertRuleId);
        if (rule is null) return; // rule was deleted since the alert fired

        var answer = await _explainer.AnswerFollowUpAsync(rule, question);
        if (answer is null) return; // no API key configured, or the call failed — stay silent

        await _chat.PostToSpaceAsync(spaceId, answer, threadName);
    }
}
