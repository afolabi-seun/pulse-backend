using System.Text.Json;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Notifications;
using Pulse.Domain.Engineers;
using Pulse.Domain.Notifications;

namespace Pulse.Application.Overwork;

/// <summary>
/// Daily digest (see backlog #6) — team leads get their own overworked team members, PMs get an
/// org-wide list (PM is already a global role for everything else in this app), and PMO gets a
/// rolled-up count rather than a per-engineer list, so they aren't paged for cases the team
/// lead/PM already own.
/// </summary>
public class OverworkDigestJob : IRecurringJob
{
    private readonly IEngineerRepository _engineers;
    private readonly IOverworkOverrideRepository _overrides;
    private readonly OverworkSignalsCalculator _calculator;
    private readonly ITeamRepository _teams;
    private readonly IDepartmentThresholdRepository _departmentThresholds;
    private readonly INotificationRepository _notifications;
    private readonly IRealtimeNotifier _realtime;
    private readonly IEmailQueue _emailQueue;
    private readonly IAppSettings _settings;

    public OverworkDigestJob(
        IEngineerRepository engineers,
        IOverworkOverrideRepository overrides,
        OverworkSignalsCalculator calculator,
        ITeamRepository teams,
        IDepartmentThresholdRepository departmentThresholds,
        INotificationRepository notifications,
        IRealtimeNotifier realtime,
        IEmailQueue emailQueue,
        IAppSettings settings)
    {
        _engineers = engineers;
        _overrides = overrides;
        _calculator = calculator;
        _teams = teams;
        _departmentThresholds = departmentThresholds;
        _notifications = notifications;
        _realtime = realtime;
        _emailQueue = emailQueue;
        _settings = settings;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var engineers = await _engineers.ListActiveAsync(ct);
        var activeOverrideIds = (await _overrides.GetAllActiveAsync(ct))
            .Select(o => o.EngineerId)
            .ToHashSet();
        var teams = (await _teams.ListAllAsync(ct)).ToDictionary(t => t.Id);
        // Loaded once for the whole run, not per engineer — every lookup below is in-memory.
        var departmentOverrides = (await _departmentThresholds.GetAllAsync(ct))
            .ToDictionary(d => d.Department, d => d, StringComparer.OrdinalIgnoreCase);

        var overworked = new List<Engineer>();
        var byTeamLead = new Dictionary<Guid, List<Engineer>>();

        foreach (var engineer in engineers)
        {
            if (activeOverrideIds.Contains(engineer.Id)) continue;

            DepartmentThresholdOverride? departmentOverride = null;
            if (engineer.TeamId is Guid engTeamId
                && teams.TryGetValue(engTeamId, out var engTeam)
                && engTeam.Department is string dept)
                departmentOverrides.TryGetValue(dept, out departmentOverride);

            var (_, activeTasks) = await _engineers.GetWithActiveTasksAsync(engineer.Id, ct);
            var (_, isOverworked) = _calculator.Compute(engineer, activeTasks, @override: null, departmentOverride);
            if (!isOverworked) continue;

            overworked.Add(engineer);

            if (engineer.TeamId is Guid teamId
                && teams.TryGetValue(teamId, out var team)
                && team.TeamLeadId is Guid leadId)
            {
                if (!byTeamLead.TryGetValue(leadId, out var list))
                    byTeamLead[leadId] = list = [];
                list.Add(engineer);
            }
        }

        if (overworked.Count == 0) return;

        foreach (var (leadId, teamMembers) in byTeamLead)
        {
            var lead = await _engineers.GetByIdAsync(leadId, ct);
            if (lead is null) continue;
            await SendListDigestAsync(lead, teamMembers, "your team", ct);
        }

        var pms = await _engineers.ListByRoleAsync(Roles.ProjectManager, ct);
        foreach (var pm in pms)
            await SendListDigestAsync(pm, overworked, "the organization", ct);

        var pmoHeads = await _engineers.ListByRoleAsync(Roles.HeadOfPmo, ct);
        foreach (var pmo in pmoHeads)
            await SendSummaryDigestAsync(pmo, overworked.Count, byTeamLead.Count, ct);
    }

    private async Task SendListDigestAsync(Engineer recipient, IReadOnlyList<Engineer> overworked, string scope, CancellationToken ct)
    {
        var names = overworked.Select(e => e.Name).ToList();
        var payload = JsonSerializer.Serialize(new { count = overworked.Count, engineerNames = names });

        var n = Notification.Create(recipient.Id, NotificationKind.OverworkDigest, payload);
        await _notifications.AddAsync(n, ct);
        await _notifications.SaveChangesAsync(ct);
        await _realtime.SendNotificationAsync(recipient.Id, NotificationDto.From(n), ct);

        var list = string.Join("", names.Select(name => $"<li>{name}</li>"));
        var body = $"""
            <p>Hi {recipient.Name},</p>
            <p>{overworked.Count} engineer(s) in {scope} are currently flagged as overworked:</p>
            <ul>{list}</ul>
            {EmailTemplate.Button($"{_settings.AppBaseUrl}/engineers", "View engineers")}
            """;
        _emailQueue.Enqueue(recipient.Email, $"Overwork digest: {overworked.Count} engineer(s) flagged", EmailTemplate.Layout(body));
    }

    private async Task SendSummaryDigestAsync(Engineer recipient, int overworkedCount, int teamsAffected, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { count = overworkedCount, teamsAffected });

        var n = Notification.Create(recipient.Id, NotificationKind.OverworkDigest, payload);
        await _notifications.AddAsync(n, ct);
        await _notifications.SaveChangesAsync(ct);
        await _realtime.SendNotificationAsync(recipient.Id, NotificationDto.From(n), ct);

        var body = $"""
            <p>Hi {recipient.Name},</p>
            <p>{overworkedCount} engineer(s) across {teamsAffected} team(s) are currently flagged as overworked.
            Team leads and PMs have the per-engineer detail.</p>
            {EmailTemplate.Button($"{_settings.AppBaseUrl}/reports", "View reports")}
            """;
        _emailQueue.Enqueue(recipient.Email, $"Overwork digest: {overworkedCount} engineer(s) org-wide", EmailTemplate.Layout(body));
    }
}
