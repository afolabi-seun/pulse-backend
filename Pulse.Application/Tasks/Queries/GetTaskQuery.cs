using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using MediatR;

namespace Pulse.Application.Tasks.Queries;

public record GetTaskQuery(Guid TaskId, Guid ActorId, string ActorRole) : IRequest<ServiceResult<TaskDto>>;

public class GetTaskHandler : IRequestHandler<GetTaskQuery, ServiceResult<TaskDto>>
{
    private readonly ITaskRepository _tasks;
    private readonly IProjectRepository _projects;
    private readonly IEngineerRepository _engineers;
    private readonly ITeamRepository _teams;
    private readonly ISubtaskRepository _subtasks;
    private readonly IProjectAccessPolicy _access;

    public GetTaskHandler(ITaskRepository tasks, IProjectRepository projects, IEngineerRepository engineers,
        ITeamRepository teams, ISubtaskRepository subtasks, IProjectAccessPolicy access)
    {
        _tasks = tasks;
        _projects = projects;
        _engineers = engineers;
        _teams = teams;
        _subtasks = subtasks;
        _access = access;
    }

    public async Task<ServiceResult<TaskDto>> Handle(GetTaskQuery query, CancellationToken ct)
    {
        var task = await _tasks.GetByIdAsync(query.TaskId, ct);
        if (task is null)
        {
            // An old link or notification to a task that has since been archived: say so rather than pretending it never existed.
            var archived = await _tasks.GetByIdIncludingArchivedAsync(query.TaskId, ct);
            return ServiceResult<TaskDto>.Fail("NOT_FOUND", archived is { IsArchived: true }
                ? "This task has been archived. A project manager can restore it."
                : $"Task '{query.TaskId}' not found.");
        }

        // Executive/HR/Accountant bypass the shared, write-coupled ProjectAccessPolicy here — see the
        // matching comment in GetProjectQuery.
        var allowed = Roles.IsOrgReadOnlyViewer(query.ActorRole)
            || await _access.CanViewTaskAsync(query.TaskId, query.ActorId, query.ActorRole, ct);
        if (!allowed)
            return ServiceResult<TaskDto>.Fail("FORBIDDEN", "You do not have access to this task.");

        var project = await _projects.GetByIdAsync(task.ProjectId, ct);
        var assignee = task.AssigneeId.HasValue ? await _engineers.GetByIdAsync(task.AssigneeId.Value, ct) : null;
        var creator = task.CreatedById.HasValue ? await _engineers.GetByIdAsync(task.CreatedById.Value, ct) : null;
        var reactivatedBy = task.ReactivatedByEngineerId.HasValue
            ? await _engineers.GetByIdAsync(task.ReactivatedByEngineerId.Value, ct) : null;
        var backendAssignee = task.BackendAssigneeId.HasValue
            ? await _engineers.GetByIdAsync(task.BackendAssigneeId.Value, ct) : null;
        var pendingRejectionActor = task.PendingRejectionActorId.HasValue
            ? await _engineers.GetByIdAsync(task.PendingRejectionActorId.Value, ct) : null;
        var pendingRejectionResponder = task.PendingRejectionRespondedByEngineerId.HasValue
            ? await _engineers.GetByIdAsync(task.PendingRejectionRespondedByEngineerId.Value, ct) : null;

        // Only meaningful for a QA sub-task — lets the frontend disable "Reject" up front
        // instead of only failing after the actor tries it.
        var canRejectQa = task.ParentTaskId.HasValue
            && await QaRejectionPolicy.CanRejectAsync(task, query.ActorId, query.ActorRole, _engineers, _teams, ct);

        var subtasks = await _subtasks.GetByTaskIdAsync(task.Id, ct);
        int? subtasksTotal = subtasks.Count > 0 ? subtasks.Count : null;
        int? subtasksDone = subtasks.Count > 0 ? subtasks.Count(s => s.IsDone) : null;

        // Only meaningful while a PR approval request is pending — lets the frontend show
        // Approve/Reject/Reassign without duplicating PrApprovalPolicy's own resolution.
        string? pendingPrApprovalRequestedByName = null;
        string? pendingPrApprovalDelegatedToName = null;
        IReadOnlyList<string>? pendingPrApprovalApproverNames = null;
        var canApprovePrApproval = false;
        if (task.PendingPrApprovalRequestedAt.HasValue)
        {
            if (task.PendingPrApprovalRequestedByEngineerId is Guid requestedById)
                pendingPrApprovalRequestedByName = (await _engineers.GetByIdAsync(requestedById, ct))?.Name;

            if (task.PendingPrApprovalDelegatedToEngineerId is Guid delegateId)
            {
                pendingPrApprovalDelegatedToName = (await _engineers.GetByIdAsync(delegateId, ct))?.Name;
                pendingPrApprovalApproverNames = pendingPrApprovalDelegatedToName is not null
                    ? [pendingPrApprovalDelegatedToName] : null;
            }
            else if (task.AssigneeId is Guid assigneeIdForHeads)
            {
                var heads = await DepartmentScope.GetDepartmentHeadsAsync(assigneeIdForHeads, _engineers, _teams, ct);
                pendingPrApprovalApproverNames = heads.Count > 0 ? heads.Select(h => h.Name).ToList() : null;
            }

            canApprovePrApproval = await PrApprovalPolicy.IsAuthorizedAsync(task, query.ActorId, query.ActorRole, _engineers, _teams, ct);
        }

        var dto = TaskDto.From(task, project?.Name, assignee?.Name, creator?.Name, canRejectQa, reactivatedBy?.Name,
            subtasksDone, subtasksTotal, project?.Code, backendAssignee?.Name,
            pendingRejectionActor?.Name, pendingRejectionResponder?.Name,
            pendingPrApprovalRequestedByName, pendingPrApprovalDelegatedToName,
            pendingPrApprovalApproverNames, canApprovePrApproval) with { IsPersonal = project?.PersonalOwnerId is not null };

        // In QA with nothing to review it: the QA task was deleted (or never existed). The page offers a way back instead of a link to nowhere.
        if (task.Status == Domain.Tasks.TaskStatus.InQa && (!task.QaTaskId.HasValue || await _tasks.GetByIdAsync(task.QaTaskId.Value, ct) is null))
            dto = dto with { QaTaskMissing = true };

        // Latest person-made move of an existing due date, with the reason given. History is
        // eager-loaded by GetByIdAsync; entries without a reason (first-time sets, system shifts,
        // pre-rule history) are skipped.
        var lastDueDateChange = task.History
            .Where(h => h.Field == "due_date" && h.OldValue is not null && !string.IsNullOrWhiteSpace(h.Reason))
            .OrderByDescending(h => h.ChangedAt)
            .FirstOrDefault();
        if (lastDueDateChange is not null)
        {
            var changedBy = await _engineers.GetByIdAsync(lastDueDateChange.ActorId, ct);
            dto = dto with
            {
                DueDateChange = new DueDateChangeDto(
                    DateOnly.TryParse(lastDueDateChange.OldValue, out var from) ? from : null,
                    DateOnly.TryParse(lastDueDateChange.NewValue, out var to) ? to : null,
                    lastDueDateChange.Reason!, lastDueDateChange.ChangedAt,
                    lastDueDateChange.ActorId, changedBy?.Name),
            };
        }

        // Same for the estimate: the latest change of an existing value that came with a reason.
        var lastPointsChange = task.History
            .Where(h => h.Field == "points" && h.OldValue is not null && h.OldValue != "0" && !string.IsNullOrWhiteSpace(h.Reason))
            .OrderByDescending(h => h.ChangedAt)
            .FirstOrDefault();
        if (lastPointsChange is not null)
        {
            var changedBy = await _engineers.GetByIdAsync(lastPointsChange.ActorId, ct);
            dto = dto with
            {
                PointsChange = new PointsChangeDto(
                    int.TryParse(lastPointsChange.OldValue, out var fromPts) ? fromPts : null,
                    int.TryParse(lastPointsChange.NewValue, out var toPts) ? toPts : null,
                    lastPointsChange.Reason!, lastPointsChange.ChangedAt,
                    lastPointsChange.ActorId, changedBy?.Name),
            };
        }

        // A QA rejection is proposed/confirmed/withdrawn via the QA sub-task's own id, but the
        // pending state itself lives on the parent (see PulseTask.ProposeQaRejection) — the
        // reviewer is looking at the sub-task page, so it needs to see that state too, borrowed
        // from the parent rather than duplicated onto the sub-task entity itself.
        if (task.ParentTaskId.HasValue)
        {
            var parent = await _tasks.GetByIdAsync(task.ParentTaskId.Value, ct);
            if (parent?.PendingRejectionReason is not null)
            {
                var proposer = parent.PendingRejectionActorId.HasValue
                    ? await _engineers.GetByIdAsync(parent.PendingRejectionActorId.Value, ct) : null;
                var responder = parent.PendingRejectionRespondedByEngineerId.HasValue
                    ? await _engineers.GetByIdAsync(parent.PendingRejectionRespondedByEngineerId.Value, ct) : null;
                dto = dto with
                {
                    PendingRejectionReason = parent.PendingRejectionReason,
                    PendingRejectionActorId = parent.PendingRejectionActorId,
                    PendingRejectionActorName = proposer?.Name,
                    PendingRejectionResponse = parent.PendingRejectionResponse,
                    PendingRejectionRespondedByEngineerId = parent.PendingRejectionRespondedByEngineerId,
                    PendingRejectionRespondedByName = responder?.Name,
                };
            }
        }

        return ServiceResult<TaskDto>.Ok(dto);
    }
}
