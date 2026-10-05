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

/// <summary>Recurring schedules. Every job runs once per organization through OrganizationJobRunner, which
/// fans out one child job per active org; job ids and schedules are unchanged, so AddOrUpdate replaces the
/// existing single-run entries in place.</summary>
public static class HangfireJobRegistrar
{
    public static void RegisterRecurringJobs(IRecurringJobManager manager)
    {
        manager.AddOrUpdate<OrganizationJobRunner<EscalationScanner>>(
            "escalation-scanner",
            r => r.RunForAllOrganizationsAsync(CancellationToken.None),
            "*/30 * * * *");

        manager.AddOrUpdate<OrganizationJobRunner<EstimateApprovalEscalationScanner>>(
            "estimate-approval-escalation-scanner",
            r => r.RunForAllOrganizationsAsync(CancellationToken.None),
            "*/30 * * * *");

        manager.AddOrUpdate<OrganizationJobRunner<AlertRuleScanner>>(
            "alert-rule-scanner",
            r => r.RunForAllOrganizationsAsync(CancellationToken.None),
            "*/30 * * * *");

        manager.AddOrUpdate<OrganizationJobRunner<AutomationRuleScanner>>(
            "automation-rule-scanner",
            r => r.RunForAllOrganizationsAsync(CancellationToken.None),
            "*/30 * * * *");

        // 08:00 weekdays — morning reminder for engineers who haven't checked in yet
        manager.AddOrUpdate<OrganizationJobRunner<CheckInReminderJob>>(
            "check-in-reminder",
            r => r.RunForAllOrganizationsAsync(CancellationToken.None),
            "0 8 * * 1-5");

        // 18:00 weekdays — end-of-day nudge for engineers still missing a check-in
        manager.AddOrUpdate<OrganizationJobRunner<CheckInNudgeJob>>(
            "check-in-nudge",
            r => r.RunForAllOrganizationsAsync(CancellationToken.None),
            "0 18 * * 1-5");

        // 09:00 every Friday — weekly vitals prompt
        manager.AddOrUpdate<OrganizationJobRunner<WeeklyVitalsPromptJob>>(
            "weekly-vitals-prompt",
            r => r.RunForAllOrganizationsAsync(CancellationToken.None),
            "0 9 * * 5");

        // 09:00 weekdays — overwork digest to team leads (own team), PMs (org-wide), PMO (rolled-up count)
        manager.AddOrUpdate<OrganizationJobRunner<OverworkDigestJob>>(
            "overwork-digest",
            r => r.RunForAllOrganizationsAsync(CancellationToken.None),
            "0 9 * * 1-5");

        // 16:00 Friday — weekly reminder for eligible roles who haven't logged any hours this week
        manager.AddOrUpdate<OrganizationJobRunner<TimeEntryReminderJob>>(
            "time-entry-reminder",
            r => r.RunForAllOrganizationsAsync(CancellationToken.None),
            "0 16 * * 5");
    }
}
