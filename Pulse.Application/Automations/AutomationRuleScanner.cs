using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Notifications;
using Pulse.Domain.Automations;
using Pulse.Domain.Notifications;

namespace Pulse.Application.Automations;

/// <summary>Scheduled evaluation of every active AutomationRule. Unlike AlertRuleScanner, this one
/// mutates: for each rule, finds tasks on its team that have been continuously Blocked past
/// ThresholdDays and reassigns them to the team's lead. The one real action this feature takes —
/// see AutomationRule's own doc comment for why it's fixed to this single action rather than a
/// selectable type.</summary>
public class AutomationRuleScanner : IRecurringJob
{
    private readonly IAutomationRuleRepository _rules;
    private readonly ITaskRepository _tasks;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;
    private readonly IAutomationExecutionRepository _executions;
    private readonly IAuditLogRepository _audit;
    private readonly IAppSettings _settings;
    private readonly INotificationDispatcher _notify;

    public AutomationRuleScanner(
        IAutomationRuleRepository rules,
        ITaskRepository tasks,
        IEngineerRepository engineers,
        ITeamRepository teams,
        IAutomationExecutionRepository executions,
        IAuditLogRepository audit,
        IAppSettings settings,
        INotificationDispatcher notify)
    {
        _notify = notify;
        _rules = rules;
        _tasks = tasks;
        _engineers = engineers;
        _teams = teams;
        _executions = executions;
        _audit = audit;
        _settings = settings;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var rules = await _rules.ListActiveAsync(ct);
        if (rules.Count == 0) return;

        var blocked = await _tasks.GetBlockedTasksAsync(ct);
        var now = DateTime.UtcNow;

        foreach (var rule in rules)
        {
            var team = await _teams.GetByIdAsync(rule.TeamId, ct);
            if (team?.TeamLeadId is not { } teamLeadId) continue; // team gone, or no lead to reassign to

            var lead = await _engineers.GetByIdAsync(teamLeadId, ct);
            if (lead is null || !lead.IsActive) continue;

            var teamEngineerIds = (await _engineers.ListAllAsync(ct))
                .Where(e => e.TeamId == rule.TeamId)
                .Select(e => e.Id)
                .ToHashSet();

            var candidates = blocked.Where(t =>
                t.AssigneeId.HasValue
                && t.AssigneeId.Value != lead.Id // already on the lead — nothing to do
                && teamEngineerIds.Contains(t.AssigneeId.Value)
                && (now - t.ActivatedAt).TotalDays >= rule.ThresholdDays);

            foreach (var task in candidates)
            {
                var lastExecution = await _executions.GetLatestAsync(rule.Id, task.Id, ct);
                // Skip if this rule already acted since the task's current blocked stretch began —
                // Assign() (called below) bumps ActivatedAt, so a later unblock+reblock naturally
                // makes any earlier execution stale and lets the rule fire again.
                if (lastExecution is not null && lastExecution.CreatedAt >= task.ActivatedAt)
                    continue;

                var previousAssigneeId = task.AssigneeId!.Value;
                var previousAssignee = await _engineers.GetByIdAsync(previousAssigneeId, ct);

                task.Assign(lead.Id, rule.OwnerEngineerId);
                await _tasks.SaveChangesAsync(ct);

                await _executions.AddAsync(AutomationExecution.Create(rule.Id, task.Id), ct);
                await _executions.SaveChangesAsync(ct);

                await _audit.LogAsync("AUTOMATION_TASK_REASSIGNED", rule.OwnerEngineerId, null,
                    $"Automation rule '{rule.Id}' reassigned task '{task.Id}' from " +
                    $"'{previousAssigneeId}' to team lead '{lead.Id}' after {rule.ThresholdDays}+ days blocked.", ct);

                await NotifyAsync(rule, task.Id, task.Title, lead, previousAssignee, ct);
            }
        }
    }

    private async Task NotifyAsync(
        AutomationRule rule, Guid taskId, string taskTitle,
        Domain.Engineers.Engineer lead, Domain.Engineers.Engineer? previousAssignee, CancellationToken ct)
    {
        await _notify.NotifyAsync(lead.Id, NotificationKind.TaskAssigned, System.Text.Json.JsonSerializer.Serialize(new { taskId, taskTitle }), ct: ct);

        var taskLink = $"{_settings.AppBaseUrl}/tasks/{taskId}";
        var body = $"""
            <p>Hi {lead.Name},</p>
            <p>A task on your team sat blocked for {rule.ThresholdDays}+ days, so Pulse's "{rule.Name}" automation reassigned it to you: <strong>{taskTitle}</strong>.</p>
            {EmailTemplate.Button(taskLink, "View task")}
            {EmailTemplate.Muted("This automation was configured in Pulse's My Automations page.")}
            """;
        await _notify.EmailAsync(lead.Id, NotificationKind.TaskAssigned, new NotificationEmail(lead.Email, $"Blocked task reassigned to you: {taskTitle}", EmailTemplate.Layout(body)), ct);

        if (previousAssignee is not null)
        {
            await _notify.NotifyAsync(previousAssignee.Id, NotificationKind.AutomationTaskReassigned, System.Text.Json.JsonSerializer.Serialize(new { taskId, taskTitle }), ct: ct);
        }
    }
}
