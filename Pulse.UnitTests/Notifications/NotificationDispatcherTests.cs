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
    private readonly Guid _recipient = Guid.NewGuid();
    private static readonly NotificationEmail Email = new("ada@acme.test", "Subject", "<p>Body</p>");

    private NotificationDispatcher Create() => new(_notifications.Object, _realtime.Object, _email.Object, _preferences.Object);

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
}
