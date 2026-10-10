using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using Pulse.Domain.Notifications;
using Pulse.Infrastructure.Notifications;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Pulse.UnitTests.Notifications;

public class SendChatNotificationJobTests
{
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<IPersonalChatSettingsRepository> _settings = new();
    private readonly Mock<ISlackClient> _slack = new();
    private readonly Mock<IGoogleChatMessenger> _googleChat = new();
    private readonly BackgroundOrganizationContext _organization = new();
    private readonly Engineer _ada = Engineer.Create("Ada", "ada@acme.test", "hash", Roles.Engineer, 20, 14);

    public SendChatNotificationJobTests() =>
        _engineers.Setup(e => e.GetByIdAsync(_ada.Id, default)).ReturnsAsync(_ada);

    private SendChatNotificationJob Create() => new(_engineers.Object, _settings.Object, _slack.Object, _googleChat.Object,
        _organization, NullLogger<SendChatNotificationJob>.Instance);

    private PersonalChatSettings Chose(string channel)
    {
        var settings = PersonalChatSettings.For(_ada.Id);
        settings.Choose(channel);
        _settings.Setup(s => s.GetAsync(_ada.Id, default)).ReturnsAsync(settings);
        return settings;
    }

    [Fact]
    public async Task Sends_a_Slack_direct_message_as_the_recipients_organization()
    {
        Chose(ChatChannel.Slack).SetSlackUser("U123");
        _slack.Setup(s => s.PostMessageAsync("U123", "Task assigned", null, default))
            .Callback(() => _organization.OrganizationId.Should().Be(_ada.OrganizationId, "the org's own Slack token must be used"))
            .ReturnsAsync(("D1", "1.0"));

        await Create().ExecuteAsync(_ada.Id, "Task assigned");

        _slack.Verify(s => s.PostMessageAsync("U123", "Task assigned", null, default));
    }

    [Fact]
    public async Task Looks_the_Slack_user_up_by_email_once_and_remembers_it()
    {
        var settings = Chose(ChatChannel.Slack);
        _slack.Setup(s => s.LookupUserIdByEmailAsync("ada@acme.test", default)).ReturnsAsync("U999");

        await Create().ExecuteAsync(_ada.Id, "hi");

        settings.SlackUserId.Should().Be("U999");
        _settings.Verify(s => s.SaveChangesAsync(default));
        _slack.Verify(s => s.PostMessageAsync("U999", "hi", null, default));
    }

    [Fact]
    public async Task Sends_a_Google_Chat_direct_message()
    {
        Chose(ChatChannel.GoogleChat).SetGoogleChatDm("spaces/DM1");

        await Create().ExecuteAsync(_ada.Id, "hi");

        _googleChat.Verify(g => g.PostToDirectMessageAsync("spaces/DM1", "hi", default));
    }

    [Fact]
    public async Task Skips_people_who_switched_chat_off_since_it_was_queued()
    {
        Chose(ChatChannel.None);

        await Create().ExecuteAsync(_ada.Id, "hi");

        _slack.VerifyNoOtherCalls();
        _googleChat.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Skips_deactivated_people()
    {
        Chose(ChatChannel.Slack).SetSlackUser("U123");
        _ada.Deactivate();

        await Create().ExecuteAsync(_ada.Id, "hi");

        _slack.VerifyNoOtherCalls();
    }
}
