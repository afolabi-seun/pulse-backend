using Hangfire;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Notifications;
using Microsoft.Extensions.Logging;

namespace Pulse.Infrastructure.Notifications;

public class HangfireChatNotificationQueue(IBackgroundJobClient jobs) : IChatNotificationQueue
{
    public void Enqueue(Guid recipientId, string text) =>
        jobs.Enqueue<SendChatNotificationJob>(j => j.ExecuteAsync(recipientId, text));
}

/// <summary>
/// Delivers one personal chat notification. Runs in the background with no organization of its own, so it
/// first resolves the recipient and then acts as the recipient's organization — the Slack token, the space
/// lookups and RLS are all that org's. Retried by Hangfire on failure; a person who has since switched chat
/// off, or can't be reached, is skipped quietly.
/// </summary>
public class SendChatNotificationJob
{
    private readonly IEngineerRepository _engineers;
    private readonly IPersonalChatSettingsRepository _settings;
    private readonly ISlackClient _slack;
    private readonly IGoogleChatMessenger _googleChat;
    private readonly BackgroundOrganizationContext _organization;
    private readonly ILogger<SendChatNotificationJob> _logger;

    public SendChatNotificationJob(IEngineerRepository engineers, IPersonalChatSettingsRepository settings, ISlackClient slack,
        IGoogleChatMessenger googleChat, BackgroundOrganizationContext organization, ILogger<SendChatNotificationJob> logger)
    {
        _engineers = engineers;
        _settings = settings;
        _slack = slack;
        _googleChat = googleChat;
        _organization = organization;
        _logger = logger;
    }

    public async Task ExecuteAsync(Guid recipientId, string text)
    {
        var recipient = await _engineers.GetByIdAsync(recipientId);
        if (recipient is null || !recipient.IsActive)
            return;
        _organization.OrganizationId = recipient.OrganizationId;

        var settings = await _settings.GetAsync(recipientId);
        switch (settings?.Channel)
        {
            case ChatChannel.Slack:
                var slackUserId = settings.SlackUserId ?? await _slack.LookupUserIdByEmailAsync(recipient.Email);
                if (slackUserId is null)
                {
                    _logger.LogInformation("No Slack user for engineer {EngineerId}; personal notification skipped.", recipientId);
                    return;
                }
                if (settings.SlackUserId is null)
                {
                    settings.SetSlackUser(slackUserId);
                    await _settings.SaveChangesAsync();
                }
                await _slack.PostMessageAsync(slackUserId, text);
                break;

            case ChatChannel.GoogleChat when settings.GoogleChatDmSpace is not null:
                await _googleChat.PostToDirectMessageAsync(settings.GoogleChatDmSpace, text);
                break;
        }
    }
}
