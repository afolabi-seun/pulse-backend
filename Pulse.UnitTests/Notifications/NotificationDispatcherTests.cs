using System.Reflection;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Notifications;
using Pulse.Domain.Notifications;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Notifications;

public class NotificationDispatcherTests
{
    private readonly Mock<INotificationRepository> _notifications = new();
    private readonly Mock<IRealtimeNotifier> _realtime = new();
    private readonly Mock<IEmailQueue> _email = new();
    private readonly Mock<INotificationPreferenceRepository> _preferences = new();
    private readonly Mock<IPersonalChatSettingsRepository> _chatSettings = new();
    private readonly Mock<IChatNotificationQueue> _chat = new();
    private readonly Guid _recipient = Guid.NewGuid();
    private static readonly NotificationEmail Email = new("ada@acme.test", "Subject", "<p>Body</p>");

    private NotificationDispatcher Create() => new(_notifications.Object, _realtime.Object, _email.Object, _preferences.Object,
        _chatSettings.Object, _chat.Object, Mock.Of<IAppSettings>(s => s.AppBaseUrl == "https://pulse.test"));

    private void ChoseChannel(string channel)
    {
        var settings = PersonalChatSettings.For(_recipient);
        settings.Choose(channel);
        _chatSettings.Setup(s => s.GetAsync(_recipient, default)).ReturnsAsync(settings);
    }

    private void EmailOff(string kind) =>
        _preferences.Setup(p => p.GetAsync(_recipient, kind, default)).ReturnsAsync(NotificationPreference.Create(_recipient, kind, false));

    [Fact]
    public async Task Records_the_notification_in_the_inbox_and_pushes_it_live()
    {
        Notification? saved = null;
        _notifications.Setup(n => n.AddAsync(It.IsAny<Notification>(), default)).Callback<Notification, CancellationToken>((n, _) => saved = n);

        await Create().NotifyAsync(_recipient, NotificationKind.TaskAssigned, new { taskId = "t1" });

        saved!.UserId.Should().Be(_recipient);
        saved.Kind.Should().Be(NotificationKind.TaskAssigned);
        saved.Payload.Should().Be("""{"taskId":"t1"}""");
        _notifications.Verify(n => n.SaveChangesAsync(default));
        _realtime.Verify(r => r.SendNotificationAsync(_recipient, It.IsAny<NotificationDto>(), default));
    }

    [Fact]
    public async Task A_string_payload_is_stored_as_is()
    {
        Notification? saved = null;
        _notifications.Setup(n => n.AddAsync(It.IsAny<Notification>(), default)).Callback<Notification, CancellationToken>((n, _) => saved = n);

        await Create().NotifyAsync(_recipient, NotificationKind.TaskAssigned, """{"already":"json"}""");

        saved!.Payload.Should().Be("""{"already":"json"}""");
    }

    [Fact]
    public async Task Emails_by_default()
    {
        await Create().NotifyAsync(_recipient, NotificationKind.TaskAssigned, null, Email);

        _email.Verify(e => e.Enqueue("ada@acme.test", "Subject", "<p>Body</p>"));
    }

    [Fact]
    public async Task Does_not_email_when_the_person_switched_that_kind_off_but_still_records_it()
    {
        EmailOff(NotificationKind.TaskAssigned);

        await Create().NotifyAsync(_recipient, NotificationKind.TaskAssigned, null, Email);

        _email.Verify(e => e.Enqueue(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _notifications.Verify(n => n.AddAsync(It.IsAny<Notification>(), default), "the inbox always records");
    }

    [Fact]
    public async Task Security_notices_are_emailed_even_if_a_preference_says_otherwise()
    {
        EmailOff(NotificationKind.AccountLocked);

        await Create().EmailAsync(_recipient, NotificationKind.AccountLocked, Email);

        _email.Verify(e => e.Enqueue("ada@acme.test", "Subject", "<p>Body</p>"));
    }

    [Fact]
    public async Task EmailAsync_sends_only_the_email_and_respects_the_preference()
    {
        await Create().EmailAsync(_recipient, NotificationKind.OverworkDigest, Email);
        _email.Verify(e => e.Enqueue("ada@acme.test", "Subject", "<p>Body</p>"), Times.Once);
        _notifications.Verify(n => n.AddAsync(It.IsAny<Notification>(), default), Times.Never);

        EmailOff(NotificationKind.OverworkDigest);
        await Create().EmailAsync(_recipient, NotificationKind.OverworkDigest, Email);
        _email.Verify(e => e.Enqueue(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void Every_notification_kind_is_in_the_catalog()
    {
        var kinds = typeof(NotificationKind).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

        kinds.Should().BeSubsetOf(NotificationCatalog.All.Select(k => k.Kind),
            "a new kind needs a catalog entry so it shows up on the preferences page");
    }

    // ── Personal chat ────────────────────────────────────────────────────

    [Fact]
    public async Task Nobody_gets_a_chat_message_until_they_choose_a_channel()
    {
        await Create().NotifyAsync(_recipient, NotificationKind.TaskAssigned, null, Email);

        _chat.Verify(c => c.Enqueue(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task A_person_who_chose_a_channel_gets_the_email_subject_and_a_link()
    {
        ChoseChannel(ChatChannel.Slack);

        await Create().NotifyAsync(_recipient, NotificationKind.TaskAssigned, null, Email);

        _chat.Verify(c => c.Enqueue(_recipient, "Subject\nhttps://pulse.test/notifications"));
    }

    [Fact]
    public async Task Without_an_email_the_chat_message_uses_the_kinds_label()
    {
        ChoseChannel(ChatChannel.GoogleChat);

        await Create().NotifyAsync(_recipient, NotificationKind.TaskReturnedToBacklog, null);

        _chat.Verify(c => c.Enqueue(_recipient, "Returned to backlog\nhttps://pulse.test/notifications"));
    }

    [Fact]
    public async Task Chat_switched_off_for_a_kind_is_respected()
    {
        ChoseChannel(ChatChannel.Slack);
        _preferences.Setup(p => p.GetAsync(_recipient, NotificationKind.TaskAssigned, default))
            .ReturnsAsync(NotificationPreference.Create(_recipient, NotificationKind.TaskAssigned, emailEnabled: true, chatEnabled: false));

        await Create().NotifyAsync(_recipient, NotificationKind.TaskAssigned, null, Email);

        _chat.Verify(c => c.Enqueue(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        _email.Verify(e => e.Enqueue("ada@acme.test", "Subject", "<p>Body</p>"), "email is a separate setting");
    }

    [Fact]
    public async Task Email_only_sends_never_post_to_chat()
    {
        // Sites that notify and email in two calls would otherwise message the person twice.
        ChoseChannel(ChatChannel.Slack);

        await Create().EmailAsync(_recipient, NotificationKind.TaskAssigned, Email);

        _chat.Verify(c => c.Enqueue(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }
}
