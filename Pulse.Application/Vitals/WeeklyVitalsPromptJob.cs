using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Notifications;
using Pulse.Domain.Notifications;

namespace Pulse.Application.Vitals;

public class WeeklyVitalsPromptJob : IRecurringJob
{
    private readonly IEngineerRepository _engineers;
    private readonly IAppSettings _settings;
    private readonly INotificationDispatcher _notify;

    public WeeklyVitalsPromptJob(
        IEngineerRepository engineers,
        IAppSettings settings,
        INotificationDispatcher notify)
    {
        _notify = notify;
        _engineers = engineers;
        _settings = settings;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var engineers = await _engineers.ListActiveAsync(ct);
        var link = $"{_settings.AppBaseUrl}/vitals";

        foreach (var engineer in engineers)
        {
            var body = $"""
                <p>Hi {engineer.Name},</p>
                <p>It's Friday! Please take 30 seconds to submit your weekly vitals in Pulse.</p>
                <p>Rate your week from 1 (very difficult) to 5 (great), and add an optional comment.</p>
                {EmailTemplate.Muted("Your response is confidential — only department heads can see it.")}
                {EmailTemplate.Button(link, "Submit your vitals")}
                """;
            await _notify.NotifyAsync(engineer.Id, NotificationKind.WeeklyVitalsPrompt, null,
                new NotificationEmail(engineer.Email, "Weekly vitals — how are you doing?", EmailTemplate.Layout(body)), ct);
        }
    }
}
