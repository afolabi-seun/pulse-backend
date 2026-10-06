using System.Text.Json;
using Pulse.Application.Auth;
using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Notifications;
using Pulse.Domain.Common;
using Pulse.Domain.Notifications;
using MediatR;

namespace Pulse.Application.Tasks.Commands;

public record ReassignPrApproverCommand(Guid TaskId, Guid NewApproverId, Guid ActorId, string? IpAddress, string ActorRole = "")
    : IRequest<ServiceResult<TaskDto>>;

public class ReassignPrApproverHandler : IRequestHandler<ReassignPrApproverCommand, ServiceResult<TaskDto>>
{
    private readonly ITaskRepository _tasks;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;
    private readonly IProjectRepository _projects;
    private readonly IAuditLogRepository _audit;
    private readonly INotificationDispatcher _notify;
    private readonly IAppSettings _settings;

    public ReassignPrApproverHandler(
        ITaskRepository tasks,
        IEngineerRepository engineers,
        ITeamRepository teams,
        IProjectRepository projects,
        IAuditLogRepository audit,
        IAppSettings settings,
        INotificationDispatcher notify)
    {
        _tasks = tasks;
        _engineers = engineers;
        _teams = teams;
        _projects = projects;
        _audit = audit;
        _notify = notify;
        _settings = settings;
    }

    public async Task<ServiceResult<TaskDto>> Handle(ReassignPrApproverCommand cmd, CancellationToken ct)
    {
        var task = await _tasks.GetByIdAsync(cmd.TaskId, ct);
        if (task is null)
            return ServiceResult<TaskDto>.Fail("NOT_FOUND", $"Task '{cmd.TaskId}' not found.");

        if (!task.PendingPrApprovalRequestedAt.HasValue)
            return ServiceResult<TaskDto>.Fail("NOT_PENDING", "No PR approval request is currently pending for this task.");

        if (!await PrApprovalPolicy.IsAuthorizedAsync(task, cmd.ActorId, cmd.ActorRole, _engineers, _teams, ct))
            return ServiceResult<TaskDto>.Fail("FORBIDDEN",
                "Only the assignee's department head (or a Team Lead, if none is set up), or whoever this request was reassigned to, can hand it off.");

        var newApprover = await _engineers.GetByIdAsync(cmd.NewApproverId, ct);
        if (newApprover is null || !newApprover.IsActive)
            return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION", "Target engineer not found or inactive.");
        if (!CapabilityRegistry.All[CapabilityRegistry.TeamLeadOrAbove].AllowedRoles.Contains(newApprover.Role))
            return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION",
                "PR approval can only be reassigned to a Team Lead or above.");

        try
        {
            task.ReassignPrApprover(cmd.NewApproverId, cmd.ActorId);
        }
        catch (DomainException ex)
        {
            return ServiceResult<TaskDto>.Fail("BUSINESS_RULE_VIOLATION", ex.Message);
        }

        await _tasks.SaveChangesAsync(ct);

        // A reassignment can cross team/department boundaries — the whole point is picking someone
        // other than the unavailable default — so grant the delegate project access the same way
        // LoanTaskCommand does, or they'd be handed an approval they can't even open the task to act on.
        await _projects.AddMemberAsync(task.ProjectId, cmd.NewApproverId, ct);

        await _audit.LogAsync("PR_APPROVAL_REASSIGNED", cmd.ActorId, cmd.IpAddress,
            $"PR approval for task '{task.Id}' reassigned to '{cmd.NewApproverId}'.", ct);

        var reassignedBy = await _engineers.GetByIdAsync(cmd.ActorId, ct);
        var payload = JsonSerializer.Serialize(new
        {
            taskId = task.Id, taskTitle = task.Title,
            prLink = task.PrLink, reassignedByName = reassignedBy?.Name,
        });
        await _notify.NotifyAsync(cmd.NewApproverId, NotificationKind.PrApprovalReassigned, payload, ct: ct);

        var taskLink = $"{_settings.AppBaseUrl}/tasks/{task.Id}";
        var body = $"""
            <p>Hi {newApprover.Name},</p>
            <p><strong>{reassignedBy?.Name ?? "A colleague"}</strong> handed you a pending PR approval for
            <strong>{task.Title}</strong>.</p>
            <p>PR: <strong>{task.PrLink}</strong></p>
            {EmailTemplate.Button(taskLink, "Review task")}
            {EmailTemplate.Muted("This notification was sent because a PR approval request was reassigned to you.")}
            """;
        await _notify.EmailAsync(newApprover.Id, NotificationKind.PrApprovalReassigned, new NotificationEmail(newApprover.Email, $"PR approval reassigned to you: {task.Title}", EmailTemplate.Layout(body)), ct);

        return ServiceResult<TaskDto>.Ok(TaskDto.From(task));
    }
}
