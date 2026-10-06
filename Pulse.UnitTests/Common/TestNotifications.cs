using Pulse.Application.Common.Interfaces;
using Pulse.Application.Notifications;
using Moq;

namespace Pulse.UnitTests.Common;

/// <summary>
/// A real NotificationDispatcher over a test's own mocks, so assertions made on the inbox, realtime and email
/// mocks keep proving what's delivered — now through the dispatcher. No preference overrides (email on).
/// </summary>
public static class TestNotifications
{
    public static INotificationDispatcher Dispatcher(
        Mock<INotificationRepository>? notifications = null, Mock<IRealtimeNotifier>? realtime = null, Mock<IEmailQueue>? email = null) =>
        new NotificationDispatcher(
            (notifications ?? new Mock<INotificationRepository>()).Object,
            (realtime ?? new Mock<IRealtimeNotifier>()).Object,
            (email ?? new Mock<IEmailQueue>()).Object,
            new Mock<INotificationPreferenceRepository>().Object);
}
