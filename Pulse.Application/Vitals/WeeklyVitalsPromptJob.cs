using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Notifications;

namespace Pulse.Application.Vitals;

public class WeeklyVitalsPromptJob : IRecurringJob
{
    private readonly IEngineerRepository _engineers;
    private readonly INotificationRepository _notifications;
    private readonly IEmailQueue _emailQueue;
    private readonly IAppSettings _settings;

    public WeeklyVitalsPromptJob(
        IEngineerRepository engineers,
        INotificationRepository notifications,
        IEmailQueue emailQueue,
        IAppSettings settings)
    {
        _engineers = engineers;
        _notifications = notifications;
        _emailQueue = emailQueue;
        _settings = settings;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var engineers = await _engineers.ListActiveAsync(ct);
        var link = $"{_settings.AppBaseUrl}/vitals";

        foreach (var engineer in engineers)
        {
            await _notifications.AddAsync(
                Notification.Create(engineer.Id, NotificationKind.WeeklyVitalsPrompt), ct);

            var body = $"""
                <p>Hi {engineer.Name},</p>
                <p>It's Friday! Please take 30 seconds to submit your weekly vitals in Pulse.</p>
                <p>Rate your week from 1 (very difficult) to 5 (great), and add an optional comment.</p>
                {EmailTemplate.Muted("Your response is confidential — only department heads can see it.")}
                {EmailTemplate.Button(link, "Submit your vitals")}
                """;
            _emailQueue.Enqueue(engineer.Email, "Weekly vitals — how are you doing?", EmailTemplate.Layout(body));
        }

        await _notifications.SaveChangesAsync(ct);
    }
}
