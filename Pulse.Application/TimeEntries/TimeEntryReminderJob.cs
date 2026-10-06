using Pulse.Application.Auth;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;

namespace Pulse.Application.TimeEntries;

/// <summary>
/// Weekly reminder (Friday 16:00) — emails eligible-role engineers who have not logged any hours
/// this week. "Eligible" mirrors CapabilityRegistry.TimeEntrySubmitter exactly, so the reminder
/// never nags a PMO or Executive user who isn't allowed to submit anyway.
/// </summary>
public class TimeEntryReminderJob : IRecurringJob
{
    private readonly ITimeEntryRepository _timeEntries;
    private readonly IEngineerRepository _engineers;
    private readonly IEmailQueue _emailQueue;
    private readonly IAppSettings _settings;

    public TimeEntryReminderJob(
        ITimeEntryRepository timeEntries,
        IEngineerRepository engineers,
        IEmailQueue emailQueue,
        IAppSettings settings)
    {
        _timeEntries = timeEntries;
        _engineers = engineers;
        _emailQueue = emailQueue;
        _settings = settings;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var eligibleRoles = CapabilityRegistry.All[CapabilityRegistry.TimeEntrySubmitter].AllowedRoles;

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var weekStart = WeekOf.Monday(today);
        var weekEnd = weekStart.AddDays(6);

        var allActive = await _engineers.ListActiveAsync(ct);
        var eligible = allActive.Where(e => eligibleRoles.Contains(e.Role)).ToList();

        var hoursByEngineer = await _timeEntries.GetHoursByEngineerInRangeAsync(
            weekStart, weekEnd, eligible.Select(e => e.Id).ToList(), ct);

        foreach (var engineer in eligible)
        {
            if (hoursByEngineer.GetValueOrDefault(engineer.Id, 0m) > 0) continue;

            var link = $"{_settings.AppBaseUrl}/my-time";
            var body = $"""
                <p>Hi {engineer.Name},</p>
                <p>You haven't logged any hours this week yet — it only takes a minute to catch up.</p>
                {EmailTemplate.Button(link, "Log your time")}
                """;

            _emailQueue.Enqueue(engineer.Email, "Pulse — weekly time log reminder", EmailTemplate.Layout(body));
        }
    }
}
