using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Notifications;
using Pulse.Domain.Notifications;

namespace Pulse.Application.CheckIns;

/// <summary>
/// Morning reminder (08:00) — emails engineers who have not yet submitted today's check-in.
/// </summary>
public class CheckInReminderJob : IRecurringJob
{
    private readonly ICheckInRepository _checkIns;
    private readonly IEngineerRepository _engineers;
    private readonly IAppSettings _settings;
    private readonly INotificationDispatcher _notify;

    public CheckInReminderJob(
        ICheckInRepository checkIns,
        IEngineerRepository engineers,
        IAppSettings settings,
        INotificationDispatcher notify)
    {
        _notify = notify;
        _checkIns = checkIns;
        _engineers = engineers;
        _settings = settings;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var missingIds = await _checkIns.GetEngineersWithoutCheckInTodayAsync(ct);

        foreach (var id in missingIds)
        {
            var engineer = await _engineers.GetByIdAsync(id, ct);
            if (engineer is null) continue;

            var link = $"{_settings.AppBaseUrl}/check-in";
            var body = $"""
                <p>Hi {engineer.Name},</p>
                <p>Don't forget to submit your daily check-in — it only takes a minute.</p>
                {EmailTemplate.Button(link, "Submit check-in")}
                """;

            await _notify.EmailAsync(engineer.Id, NotificationKind.CheckInReminder, new NotificationEmail(engineer.Email, "Pulse — daily check-in reminder", EmailTemplate.Layout(body)), ct);
        }
    }
}
