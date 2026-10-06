using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Notifications;
using MediatR;

namespace Pulse.Application.Notifications.Preferences;

/// <param name="Channel">none, slack or google_chat.</param>
/// <param name="SlackAvailable">The person's organization has connected Slack.</param>
/// <param name="GoogleChatAvailable">Google Chat is set up on this server.</param>
/// <param name="GoogleChatLinked">The person has opened a direct message with Pulse in Google Chat.</param>
public record PersonalChatSettingsDto(string Channel, bool SlackAvailable, bool GoogleChatAvailable, bool GoogleChatLinked);

public record GetPersonalChatSettingsQuery(Guid UserId) : IRequest<ServiceResult<PersonalChatSettingsDto>>;

public class GetPersonalChatSettingsHandler(IPersonalChatSettingsRepository settings, ISlackInstallationRepository slack,
    ICurrentUserService currentUser, IAppSettings appSettings)
    : IRequestHandler<GetPersonalChatSettingsQuery, ServiceResult<PersonalChatSettingsDto>>
{
    public async Task<ServiceResult<PersonalChatSettingsDto>> Handle(GetPersonalChatSettingsQuery query, CancellationToken ct)
    {
        var mine = await settings.GetAsync(query.UserId, ct);
        var slackConnected = currentUser.OrganizationId is Guid orgId && await slack.GetByOrganizationAsync(orgId, ct) is not null;
        return ServiceResult<PersonalChatSettingsDto>.Ok(new PersonalChatSettingsDto(
            mine?.Channel ?? ChatChannel.None,
            slackConnected,
            !string.IsNullOrEmpty(appSettings.GoogleChatServiceAccountJson),
            mine?.GoogleChatDmSpace is not null));
    }
}

public record UpdatePersonalChatChannelCommand(Guid UserId, string Channel) : IRequest<ServiceResult<PersonalChatSettingsDto>>;

/// <summary>Chooses where personal notifications are also sent. Checked up front, so a person isn't left
/// with a channel that can never reach them.</summary>
public class UpdatePersonalChatChannelHandler(IPersonalChatSettingsRepository settings, IEngineerRepository engineers,
    ISlackInstallationRepository slack, ISlackClient slackClient, ICurrentUserService currentUser, IAppSettings appSettings)
    : IRequestHandler<UpdatePersonalChatChannelCommand, ServiceResult<PersonalChatSettingsDto>>
{
    public async Task<ServiceResult<PersonalChatSettingsDto>> Handle(UpdatePersonalChatChannelCommand cmd, CancellationToken ct)
    {
        if (!ChatChannel.All.Contains(cmd.Channel))
            return ServiceResult<PersonalChatSettingsDto>.Fail("VALIDATION_ERROR", $"Channel must be one of: {string.Join(", ", ChatChannel.All)}.");

        var mine = await settings.GetOrCreateAsync(cmd.UserId, ct);
        var slackConnected = currentUser.OrganizationId is Guid orgId && await slack.GetByOrganizationAsync(orgId, ct) is not null;

        if (cmd.Channel == ChatChannel.Slack)
        {
            if (!slackConnected)
                return Fail("Your organization hasn't connected Slack yet.");
            var me = await engineers.GetByIdAsync(cmd.UserId, ct);
            var slackUserId = me is null ? null : await slackClient.LookupUserIdByEmailAsync(me.Email, ct);
            if (slackUserId is null)
                return Fail("We couldn't find a Slack account with your email address in your organization's workspace.");
            mine.SetSlackUser(slackUserId);
        }
        else if (cmd.Channel == ChatChannel.GoogleChat && mine.GoogleChatDmSpace is null)
        {
            return Fail("Open a direct message with Pulse in Google Chat first, then choose Google Chat here.");
        }

        mine.Choose(cmd.Channel);
        await settings.SaveChangesAsync(ct);
        return ServiceResult<PersonalChatSettingsDto>.Ok(new PersonalChatSettingsDto(mine.Channel, slackConnected,
            !string.IsNullOrEmpty(appSettings.GoogleChatServiceAccountJson), mine.GoogleChatDmSpace is not null));
    }

    private static ServiceResult<PersonalChatSettingsDto> Fail(string message) =>
        ServiceResult<PersonalChatSettingsDto>.Fail("BUSINESS_RULE_VIOLATION", message);
}

/// <summary>
/// Sent by the Google Chat events endpoint for any event in a direct message with Pulse. Links that DM to the
/// Pulse account with the same email — Google verified the email (the request is signed), and emails are
/// unique across organizations — so the person can then choose Google Chat for their notifications. Anonymous:
/// no caller organization. Always returns the message to reply in the DM.
/// </summary>
public record LinkGoogleChatDirectMessageCommand(string Email, string DmSpace) : IRequest<ServiceResult<string>>;

public class LinkGoogleChatDirectMessageHandler(IEngineerRepository engineers, IPersonalChatSettingsRepository settings)
    : IRequestHandler<LinkGoogleChatDirectMessageCommand, ServiceResult<string>>
{
    public async Task<ServiceResult<string>> Handle(LinkGoogleChatDirectMessageCommand cmd, CancellationToken ct)
    {
        var engineer = await engineers.GetByEmailAsync(cmd.Email, ct);
        if (engineer is null || !engineer.IsActive)
            return ServiceResult<string>.Ok($"I couldn't find a Pulse account for {cmd.Email}.");

        var mine = await settings.GetOrCreateAsync(engineer.Id, ct);
        if (mine.GoogleChatDmSpace == cmd.DmSpace)
            return ServiceResult<string>.Ok(mine.Channel == ChatChannel.GoogleChat
                ? "You're all set — your Pulse notifications come here."
                : "You're linked. Choose Google Chat under Notification preferences in Pulse to get your notifications here.");

        mine.SetGoogleChatDm(cmd.DmSpace);
        await settings.SaveChangesAsync(ct);
        return ServiceResult<string>.Ok(
            $"Hi {engineer.Name}! This chat is now linked to your Pulse account. Choose Google Chat under Notification preferences in Pulse to get your notifications here.");
    }
}
