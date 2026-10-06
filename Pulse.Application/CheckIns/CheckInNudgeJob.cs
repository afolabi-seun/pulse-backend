using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;

namespace Pulse.Application.CheckIns;

/// <summary>
/// Evening nudge (18:00) — a softer follow-up for engineers who still haven't checked in by end of day.
/// </summary>
public class CheckInNudgeJob : IRecurringJob
{
    private readonly ICheckInRepository _checkIns;
    private readonly IEngineerRepository _engineers;
    private readonly IEmailQueue _emailQueue;
    private readonly IAppSettings _settings;

    public CheckInNudgeJob(
        ICheckInRepository checkIns,
        IEngineerRepository engineers,
        IEmailQueue emailQueue,
        IAppSettings settings)
    {
        _checkIns = checkIns;
        _engineers = engineers;
        _emailQueue = emailQueue;
        _settings = settings;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        // Skip weekends when looking for a prior miss — Sunday is not a workday,
        // so Monday's nudge should compare against Friday, not Sunday.
        var prevWorkday = today.DayOfWeek == DayOfWeek.Monday ? today.AddDays(-3) : today.AddDays(-1);
        var missingIds = await _checkIns.GetEngineersWithoutCheckInTodayAsync(ct);

        foreach (var id in missingIds)
        {
            var engineer = await _engineers.GetByIdAsync(id, ct);
            if (engineer is null) continue;

            // Any row counts as "checked in" for that day — an auto check-in now carries the
            // completed task's real ProjectId, not null, so this must check for existence across
            // all projects rather than the project-less row specifically.
            var yesterdaysCheckIns = await _checkIns.GetByDateAsync(prevWorkday, [id], ct);
            var alsoMissedYesterday = yesterdaysCheckIns.Count == 0;
            var link = $"{_settings.AppBaseUrl}/check-in";

            string subject;
            string body;

            if (alsoMissedYesterday)
            {
                // Two or more consecutive misses — softer, check-in tone
                subject = "Pulse — quick check-in when you get a moment";
                body = $"""
                    <p>Hi {engineer.Name},</p>
                    <p>We noticed you haven't checked in for a couple of days. No pressure — just a gentle reminder that it helps the team stay in sync.</p>
                    <p>If anything is getting in the way, your PM is happy to help.</p>
                    {EmailTemplate.Button(link, "Submit check-in")}
                    """;
            }
            else
            {
                subject = "Pulse — end-of-day check-in nudge";
                body = $"""
                    <p>Hi {engineer.Name},</p>
                    <p>End-of-day nudge — your check-in is still missing for today.</p>
                    <p>It helps the team stay in sync. Takes less than 60 seconds.</p>
                    {EmailTemplate.Button(link, "Submit check-in")}
                    """;
            }

            _emailQueue.Enqueue(engineer.Email, subject, EmailTemplate.Layout(body));
        }
    }
}
