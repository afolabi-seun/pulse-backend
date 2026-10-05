using Pulse.Application.Alerts;
using Pulse.Application.Automations;
using Pulse.Application.CheckIns;
using Pulse.Application.Escalations;
using Pulse.Application.Estimation;
using Pulse.Application.Overwork;
using Pulse.Application.Vitals;
using Pulse.Application.TimeEntries;
using Hangfire;

namespace Pulse.Infrastructure.BackgroundJobs;

public static class HangfireJobRegistrar
{
    public static void RegisterRecurringJobs(IRecurringJobManager manager)
    {
        manager.AddOrUpdate<EscalationScanner>(
            "escalation-scanner",
            j => j.RunAsync(CancellationToken.None),
            "*/30 * * * *");

        manager.AddOrUpdate<EstimateApprovalEscalationScanner>(
            "estimate-approval-escalation-scanner",
            j => j.RunAsync(CancellationToken.None),
            "*/30 * * * *");

        manager.AddOrUpdate<AlertRuleScanner>(
            "alert-rule-scanner",
            j => j.RunAsync(CancellationToken.None),
            "*/30 * * * *");

        manager.AddOrUpdate<AutomationRuleScanner>(
            "automation-rule-scanner",
            j => j.RunAsync(CancellationToken.None),
            "*/30 * * * *");

        // 08:00 weekdays — morning reminder for engineers who haven't checked in yet
        manager.AddOrUpdate<CheckInReminderJob>(
            "check-in-reminder",
            j => j.RunAsync(CancellationToken.None),
            "0 8 * * 1-5");

        // 18:00 weekdays — end-of-day nudge for engineers still missing a check-in
        manager.AddOrUpdate<CheckInNudgeJob>(
            "check-in-nudge",
            j => j.RunAsync(CancellationToken.None),
            "0 18 * * 1-5");

        // 09:00 every Friday — weekly vitals prompt
        manager.AddOrUpdate<WeeklyVitalsPromptJob>(
            "weekly-vitals-prompt",
            j => j.RunAsync(CancellationToken.None),
            "0 9 * * 5");

        // 09:00 weekdays — overwork digest to team leads (own team), PMs (org-wide), PMO (rolled-up count)
        manager.AddOrUpdate<OverworkDigestJob>(
            "overwork-digest",
            j => j.RunAsync(CancellationToken.None),
            "0 9 * * 1-5");

        // 16:00 Friday — weekly reminder for eligible roles who haven't logged any hours this week
        manager.AddOrUpdate<TimeEntryReminderJob>(
            "time-entry-reminder",
            j => j.RunAsync(CancellationToken.None),
            "0 16 * * 5");
    }
}
