using System.Security.Claims;
using Asp.Versioning;
using Pulse.Api.Attributes;
using Pulse.Api.Authorization;
using Pulse.Application.Auth;
using Pulse.Api.Common;
using Pulse.Application.Common;
using Pulse.Application.Engineers;
using Pulse.Application.Engineers.Queries;
using Pulse.Application.Overwork;
using Pulse.Application.Tasks;
using Pulse.Application.Tasks.Archive;
using Pulse.Application.Tasks.Commands;
using Pulse.Application.Tasks.Queries;
using Pulse.Domain.Tasks;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Pulse.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/tasks")]
[Tags("Tasks")]
[EngineerAuth]
public class TasksController : ControllerBase
{
    private readonly IMediator _mediator;

    public TasksController(IMediator mediator) => _mediator = mediator;

    public record CreateTaskRequest(
        string Title,
        string? Description,
        int? Points,
        DateOnly? DueDate,
        Guid ProjectId,
        Guid? AssigneeId,
        string? Type,
        Guid? EpicId,
        bool RequiresQa = false,
        string? Discipline = null,
        Guid? ParentTaskId = null,
        int? Priority = null,
        string? AcceptanceCriteria = null,
        string? ExternalReference = null,
        bool RequiresFrontendHandoff = false,
        // HR/Accountant only: a private to-do in the caller's own personal project. ProjectId is ignored.
        bool Personal = false);

    public record UpdateTaskRequest(
        string? Title,
        string? Description,
        string? AcceptanceCriteria,
        string? Severity,
        Guid? EpicId,
        bool? RemoveFromEpic,
        int? Points,
        DateOnly? DueDate,
        DateOnly? ActualEndDate,
        Guid? AssigneeId,
        string? Type,
        bool? MarkDone,
        Guid? SprintId,
        bool? RemoveFromSprint,
        string? Status,
        string? BlockerReason,
        bool? RequiresQa = null,
        string? Discipline = null,
        bool? RequiresFrontendHandoff = null,
        string? PauseNote = null,
        int? Priority = null,
        bool? RemovePriority = null,
        string? ExternalReference = null,
        bool? RemoveExternalReference = null,
        bool? RequiresPrApproval = null,
        string? DueDateChangeReason = null,
        string? PointsChangeReason = null);

    public record FlagBlockerRequest(string Reason);
    public record RequestPrApprovalRequest(string PrLink);
    public record RejectPrApprovalRequest(string? Reason);
    public record ReassignPrApproverRequest(Guid ApproverId);
    public record HandOffToFrontendRequest(Guid FrontendAssigneeId);
    public record HandOffToBackendRequest(Guid BackendAssigneeId);
    public record PauseTaskRequest(string? Note);
    public record PreviewAssignRequest(Guid TargetEngineerId);
    public record BulkReassignRequest(IReadOnlyList<Guid> TaskIds, Guid TargetEngineerId);
    public record BulkStatusChangeRequest(IReadOnlyList<Guid> TaskIds, string TargetStatus);
    public record BulkCreateTaskItem(string Title, string? Description, int? Points, DateOnly? DueDate, Guid? AssigneeId, string? Type, string? AcceptanceCriteria = null);
    public record BulkCreateTasksRequest(Guid ProjectId, IReadOnlyList<BulkCreateTaskItem> Tasks);
    public record LoanTaskRequest(Guid TargetEngineerId, string? Reason);

    /// <summary>Returns a paginated list of tasks.</summary>
    /// <remarks>
    /// Engineers see only tasks assigned to them. Team leads and above see all tasks.
    /// Filter by projectId, assigneeId (PM+ only), or status. excludeDone hides Done tasks
    /// regardless of the status filter — used for "active work" views like the dashboard.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<TaskDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ListTasks(
        [FromQuery] Guid? projectId,
        [FromQuery] Guid? assigneeId,
        [FromQuery] string? status,
        [FromQuery] string? taskType,
        [FromQuery] Guid? sprintId,
        [FromQuery] Guid? epicId,
        [FromQuery] bool noSprint = false,
        [FromQuery] int limit = 25,
        [FromQuery] string? cursor = null,
        [FromQuery] string? title = null,
        [FromQuery] string? discipline = null,
        [FromQuery] bool excludeDone = false,
        [FromQuery] bool noAssignee = false,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? sortDirection = null)
    {
        Discipline? parsedDiscipline = discipline is not null && Enum.TryParse<Discipline>(discipline, ignoreCase: true, out var d) ? d : null;
        return (await _mediator.Send(new ListTasksQuery(
            GetActorId(), GetRole(), projectId, assigneeId, status, taskType, sprintId, epicId, limit, cursor, noSprint, title, parsedDiscipline, excludeDone, noAssignee, sortBy, sortDirection))).ToActionResult();
    }

    /// <summary>Returns the caller's open QA review queue — QA sub-tasks assigned to them, across every
    /// project (including ones they aren't a member of, since QA auto-assignment doesn't grant membership).</summary>
    [HttpGet("qa-queue")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<TaskDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetQaQueue() =>
        (await _mediator.Send(new GetQaQueueQuery(GetActorId()))).ToActionResult();

    /// <summary>Returns tasks the caller most recently did backend work on that are no longer
    /// assigned to them — handed off to Frontend and not yet handed back — so their finished work
    /// stays visible even though the task's current assignee has moved on.</summary>
    [HttpGet("my-frontend-handoffs")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<TaskDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetMyFrontendHandoffs() =>
        (await _mediator.Send(new ListMyFrontendHandoffsQuery(GetActorId()))).ToActionResult();

    /// <summary>Creates a new task in the given project. Any authenticated user with access to the project (member, owning-team, or PM/above) may create tasks in it.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<TaskDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> CreateTask([FromBody] CreateTaskRequest request)
    {
        var type = Enum.TryParse<TaskType>(request.Type, ignoreCase: true, out var parsedType)
            ? parsedType
            : TaskType.Feature;

        Discipline? discipline = request.Discipline is not null && Enum.TryParse<Discipline>(request.Discipline, ignoreCase: true, out var parsedDisc)
            ? parsedDisc : null;

        var result = await _mediator.Send(new CreateTaskCommand(
            request.Title, request.Description, request.Points, request.DueDate,
            request.ProjectId, request.AssigneeId, type, request.EpicId,
            request.RequiresQa, discipline, request.ParentTaskId, GetActorId(), GetIp(), GetRole(),
            request.Priority, request.AcceptanceCriteria, request.ExternalReference, request.RequiresFrontendHandoff,
            request.Personal));

        return result.ToCreatedResult("GetTask", new { id = result.Data?.Id });
    }

    /// <summary>Returns a single task by ID including its change history.</summary>
    [HttpGet("{id:guid}", Name = "GetTask")]
    [ProducesResponseType(typeof(ApiResponse<TaskDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetTask(Guid id) =>
        (await _mediator.Send(new GetTaskQuery(id, GetActorId(), GetRole()))).ToActionResult();

    /// <summary>Lists everyone mentionable in this task's comments and blocker reason — everyone
    /// who can access its project (PM+/global roles, members, task assignees, the owning team,
    /// a matching department head), plus the task's own creator.</summary>
    [HttpGet("{id:guid}/mention-candidates")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<EngineerDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetMentionCandidates(Guid id) =>
        (await _mediator.Send(new GetTaskMentionCandidatesQuery(id, GetActorId(), GetRole()))).ToActionResult();

    /// <summary>Partially updates a task — title, description, points, due date, assignee, or status.</summary>
    /// <remarks>
    /// All fields are optional; only supplied fields are applied.
    /// Cannot edit a done task. Setting MarkDone=true transitions to Done (irreversible).
    /// </remarks>
    [HttpPatch("{id:guid}")]
    [RequiresCapability(CapabilityRegistry.TeamLeadOrAbove)]
    [ProducesResponseType(typeof(ApiResponse<TaskDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> UpdateTask(Guid id, [FromBody] UpdateTaskRequest request)
    {
        TaskType? type = request.Type is not null && Enum.TryParse<TaskType>(request.Type, ignoreCase: true, out var parsed)
            ? parsed
            : null;

        BugSeverity? severity = request.Severity is not null && Enum.TryParse<BugSeverity>(request.Severity, ignoreCase: true, out var parsedSev)
            ? parsedSev
            : null;

        Discipline? discipline = request.Discipline is not null && Enum.TryParse<Discipline>(request.Discipline, ignoreCase: true, out var parsedDisc)
            ? parsedDisc : null;

        return (await _mediator.Send(new UpdateTaskCommand(
            id, request.Title, request.Description, request.AcceptanceCriteria, severity,
            request.EpicId, request.RemoveFromEpic,
            request.Points, request.DueDate, request.ActualEndDate,
            request.AssigneeId, type, request.MarkDone,
            request.SprintId, request.RemoveFromSprint,
            request.Status, request.BlockerReason, request.PauseNote, request.RequiresQa, discipline,
            GetActorId(), GetIp(), GetRole(), request.Priority, request.RemovePriority,
            request.ExternalReference, request.RemoveExternalReference, request.RequiresFrontendHandoff,
            request.RequiresPrApproval, request.DueDateChangeReason, request.PointsChangeReason))).ToActionResult();
    }

    public record GroomOwnTaskRequest(int Points, int? Priority);

    /// <summary>Self-service grooming for an engineer's own self-created task — the only way, short of
    /// a Team Lead+ using the general PATCH endpoint, for an Engineer/Designer to add points to a task
    /// they created and are still assigned to, moving it out of Backlog. Only Points/Priority; only
    /// while the task is still ungroomed; only the task's own creator.</summary>
    [HttpPatch("{id:guid}/groom")]
    [ProducesResponseType(typeof(ApiResponse<TaskDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GroomOwnTask(Guid id, [FromBody] GroomOwnTaskRequest request) =>
        (await _mediator.Send(new GroomOwnTaskCommand(id, request.Points, request.Priority, GetActorId(), GetIp()))).ToActionResult();

    public record AssigneeEditTaskRequest(string? Description, DateOnly? DueDate, string? DueDateChangeReason = null);

    /// <summary>Self-service editing for the task's own assignee — description and due date only.
    /// Points are deliberately out of reach here; see the Planning Poker endpoints for those.</summary>
    [HttpPatch("{id:guid}/assignee-edit")]
    [ProducesResponseType(typeof(ApiResponse<TaskDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> AssigneeEditTask(Guid id, [FromBody] AssigneeEditTaskRequest request) =>
        (await _mediator.Send(new AssigneeEditTaskCommand(id, request.Description, request.DueDate, GetActorId(), GetIp(), request.DueDateChangeReason))).ToActionResult();

    /// <summary>Deletes a task permanently. All history is also removed.</summary>
    [HttpDelete("{id:guid}")]
    [RequiresCapability(CapabilityRegistry.PmOrAbove)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DeleteTask(Guid id)
    {
        var result = await _mediator.Send(new DeleteTaskCommand(id, GetActorId(), GetIp(), GetRole()));
        return result.IsSuccess ? NoContent() : result.ToActionResult();
    }

    public record ArchiveTasksRequest(
        DateOnly BaselineStart,
        IReadOnlyList<Guid>? ProjectIds = null,
        bool IncludeTouched = false,
        bool DryRun = true,
        string? Reason = null,
        // Minutes from UTC of the local time the baseline day starts in (the admin's own); organizations are in different time zones.
        int UtcOffsetMinutes = 0);

    /// <summary>Archives every task created before a baseline day so the system's working data starts fresh from it. Reversible; nothing is deleted.</summary>
    /// <remarks>
    /// DryRun defaults to true: send DryRun=false with a Reason to actually archive. Tasks in personal projects are never touched. A task that has had any
    /// activity since the baseline day began (history, comment, time logged) is kept unless IncludeTouched is set; a task and its QA task move together.
    /// </remarks>
    [HttpPost("archive")]
    [RequiresCapability(CapabilityRegistry.PmoOnly)]
    [ProducesResponseType(typeof(ApiResponse<ArchiveTasksResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ArchiveTasks([FromBody] ArchiveTasksRequest request) =>
        (await _mediator.Send(new ArchiveTasksCommand(
            request.BaselineStart, request.ProjectIds, request.IncludeTouched, request.DryRun, request.Reason,
            GetActorId(), GetIp(), GetRole(), request.UtcOffsetMinutes))).ToActionResult();

    /// <summary>Lists archived tasks, newest archived first.</summary>
    [HttpGet("archived")]
    [RequiresCapability(CapabilityRegistry.PmoOnly)]
    [ProducesResponseType(typeof(ApiResponse<ArchivedTaskPage>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListArchivedTasks(
        [FromQuery] Guid? projectId, [FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 25) =>
        (await _mediator.Send(new ListArchivedTasksQuery(projectId, search, page, pageSize))).ToActionResult();

    /// <summary>Restores an archived task together with its QA task (or its parent, when given the QA task).</summary>
    [HttpPost("{id:guid}/restore")]
    [RequiresCapability(CapabilityRegistry.PmoOnly)]
    [ProducesResponseType(typeof(ApiResponse<int>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> RestoreTask(Guid id) =>
        (await _mediator.Send(new RestoreTaskCommand(id, GetActorId(), GetIp(), GetRole()))).ToActionResult();

    public record SendToQaRequest(Guid? QaEngineerId = null);

    /// <summary>Recovers a task stuck In QA because its QA task no longer exists: returns it to Active and drops the dead link so it can be sent to QA again.</summary>
    /// <remarks>Refused (422) when the QA task does exist, or when the task is not in QA.</remarks>
    [HttpPost("{id:guid}/recover-missing-qa")]
    [ProducesResponseType(typeof(ApiResponse<TaskDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RecoverMissingQa(Guid id) =>
        (await _mediator.Send(new RecoverMissingQaTaskCommand(id, GetActorId(), GetIp(), GetRole()))).ToActionResult();

    /// <summary>Sends a task to QA. Creates a linked [QA] task and sets the original to InQa status.</summary>
    /// <remarks>Task must have requiresQa set to true and must be in Active or Blocked status. An
    /// explicit QaEngineerId overrides the automatic reviewer pick; omit it to keep the previous
    /// auto-assign behavior.</remarks>
    [HttpPost("{id:guid}/send-to-qa")]
    [ProducesResponseType(typeof(ApiResponse<TaskDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SendToQa(Guid id, [FromBody] SendToQaRequest? request) =>
        (await _mediator.Send(new SendToQaCommand(id, GetActorId(), GetIp(), GetRole(), request?.QaEngineerId))).ToActionResult();

    /// <summary>Returns the active QA engineers a task can be sent to, plus the id of who the
    /// automatic pick would choose — the "Send to QA" reviewer picker.</summary>
    /// <remarks>Open to the task's own assignee (who triggers the send) as well as anyone with
    /// access to the project — unlike the PM-only /engineers/qa-candidates roster.</remarks>
    [HttpGet("{id:guid}/qa-send-candidates")]
    [ProducesResponseType(typeof(ApiResponse<QaSendCandidatesDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetQaSendCandidates(Guid id) =>
        (await _mediator.Send(new GetQaSendCandidatesQuery(id, GetActorId(), GetRole()))).ToActionResult();

    /// <summary>Submits a PR link for department-head approval.</summary>
    /// <remarks>Task must have requiresPrApproval set to true. Assignee, or anyone with project access.</remarks>
    [HttpPost("{id:guid}/pr-approval/request")]
    [ProducesResponseType(typeof(ApiResponse<TaskDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RequestPrApproval(Guid id, [FromBody] RequestPrApprovalRequest request) =>
        (await _mediator.Send(new RequestPrApprovalCommand(id, request.PrLink, GetActorId(), GetIp(), GetRole()))).ToActionResult();

    /// <summary>Approves a pending PR approval request, clearing the gate on MarkDone.</summary>
    /// <remarks>Authorization is data-driven — see PrApprovalPolicy — not a fixed role list.</remarks>
    [HttpPost("{id:guid}/pr-approval/approve")]
    [ProducesResponseType(typeof(ApiResponse<TaskDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ApprovePrApproval(Guid id) =>
        (await _mediator.Send(new ApprovePrApprovalCommand(id, GetActorId(), GetIp(), GetRole()))).ToActionResult();

    /// <summary>Rejects a pending PR approval request — clears the pending request and PR link so
    /// the requester resubmits with a fresh link.</summary>
    /// <remarks>Authorization is data-driven — see PrApprovalPolicy — not a fixed role list.</remarks>
    [HttpPost("{id:guid}/pr-approval/reject")]
    [ProducesResponseType(typeof(ApiResponse<TaskDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RejectPrApproval(Guid id, [FromBody] RejectPrApprovalRequest request) =>
        (await _mediator.Send(new RejectPrApprovalCommand(id, request.Reason, GetActorId(), GetIp(), GetRole()))).ToActionResult();

    /// <summary>Hands a pending PR approval request to a different engineer — e.g. when the
    /// assignee's department head is unavailable.</summary>
    /// <remarks>Authorization is data-driven — see PrApprovalPolicy — not a fixed role list.</remarks>
    [HttpPost("{id:guid}/pr-approval/reassign")]
    [ProducesResponseType(typeof(ApiResponse<TaskDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ReassignPrApprover(Guid id, [FromBody] ReassignPrApproverRequest request) =>
        (await _mediator.Send(new ReassignPrApproverCommand(id, request.ApproverId, GetActorId(), GetIp(), GetRole()))).ToActionResult();

    /// <summary>Returns the engineers a task can be handed off to: active engineers on its project
    /// whose discipline is <c>frontend</c> or <c>backend</c>.</summary>
    /// <remarks>Open to the task's own assignee (who performs the hand-off) as well as anyone with
    /// access to the project — unlike the PMO-only project-assignable roster.</remarks>
    [HttpGet("{id:guid}/handoff-candidates")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<HandoffCandidateDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetHandoffCandidates(Guid id, [FromQuery] string discipline) =>
        (await _mediator.Send(new GetHandoffCandidatesQuery(id, discipline, GetActorId(), GetRole()))).ToActionResult();

    /// <summary>Hands a RequiresFrontendHandoff task from its current (backend) assignee to a frontend
    /// developer. Reassigns the task in place — status is untouched, so board columns, escalation, and
    /// active-workload counting need no awareness of this at all.</summary>
    /// <remarks>Task must have requiresFrontendHandoff set to true and currentStage must be "backend".</remarks>
    [HttpPost("{id:guid}/hand-off-to-frontend")]
    [ProducesResponseType(typeof(ApiResponse<TaskDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> HandOffToFrontend(Guid id, [FromBody] HandOffToFrontendRequest request) =>
        (await _mediator.Send(new HandOffToFrontendCommand(id, request.FrontendAssigneeId, GetActorId(), GetIp(), GetRole()))).ToActionResult();

    /// <summary>The reverse of hand-off-to-frontend — sends a task back to a backend developer, e.g.
    /// after picking the wrong frontend engineer. Reassigns the task in place — status is untouched.</summary>
    /// <remarks>Task must have requiresFrontendHandoff set to true and currentStage must be "frontend".</remarks>
    [HttpPost("{id:guid}/hand-off-to-backend")]
    [ProducesResponseType(typeof(ApiResponse<TaskDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> HandOffToBackend(Guid id, [FromBody] HandOffToBackendRequest request) =>
        (await _mediator.Send(new HandOffToBackendCommand(id, request.BackendAssigneeId, GetActorId(), GetIp(), GetRole()))).ToActionResult();

    public record RespondToQaRejectionRequest(string Response);

    /// <summary>TargetStage is only meaningful on a RequiresFrontendHandoff task — "backend" while
    /// the task is currently at the Frontend stage auto-routes it to BackendAssigneeId on confirm
    /// (see ConfirmQaRejectionCommand); any other combination is purely informational.</summary>
    public record ProposeQaRejectionRequest(string Reason, string? TargetStage = null);

    /// <summary>QA flags a problem with the review — the original task stays in QA and nothing
    /// about its status changes yet, giving the assignee a chance to respond first.</summary>
    /// <remarks>Must be called on the QA sub-task (the [QA] task), not the original.</remarks>
    [HttpPost("{id:guid}/propose-qa-rejection")]
    [ProducesResponseType(typeof(ApiResponse<TaskDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ProposeQaRejection(Guid id, [FromBody] ProposeQaRejectionRequest request)
    {
        TaskStage? targetStage = request.TargetStage is not null && Enum.TryParse<TaskStage>(request.TargetStage, ignoreCase: true, out var parsedStage)
            ? parsedStage : null;
        return (await _mediator.Send(new ProposeQaRejectionCommand(id, request.Reason, GetActorId(), GetIp(), GetRole(), targetStage))).ToActionResult();
    }

    /// <summary>The assignee's reply to a pending QA rejection.</summary>
    /// <remarks>Called on the original task, not the QA sub-task.</remarks>
    [HttpPost("{id:guid}/respond-to-qa-rejection")]
    [ProducesResponseType(typeof(ApiResponse<TaskDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RespondToQaRejection(Guid id, [FromBody] RespondToQaRejectionRequest request) =>
        (await _mediator.Send(new RespondToQaRejectionCommand(id, request.Response, GetActorId(), GetIp(), GetRole()))).ToActionResult();

    /// <summary>QA confirms a proposed rejection stands — re-activates the original task with the
    /// proposed reason. This is the point a QA iteration actually counts.</summary>
    /// <remarks>Must be called on the QA sub-task (the [QA] task), not the original.</remarks>
    [HttpPost("{id:guid}/confirm-qa-rejection")]
    [ProducesResponseType(typeof(ApiResponse<TaskDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ConfirmQaRejection(Guid id) =>
        (await _mediator.Send(new ConfirmQaRejectionCommand(id, GetActorId(), GetIp(), GetRole()))).ToActionResult();

    /// <summary>QA withdraws a proposed rejection — the original task stays exactly where it was
    /// in QA, and no iteration is counted.</summary>
    /// <remarks>Must be called on the QA sub-task (the [QA] task), not the original.</remarks>
    [HttpPost("{id:guid}/withdraw-qa-rejection")]
    [ProducesResponseType(typeof(ApiResponse<TaskDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> WithdrawQaRejection(Guid id) =>
        (await _mediator.Send(new WithdrawQaRejectionCommand(id, GetActorId(), GetIp(), GetRole()))).ToActionResult();

    /// <summary>Flags a blocker on a task.</summary>
    /// <remarks>
    /// The assignee, or anyone with general access to the task's project (PMO, department heads,
    /// teammates), may flag a blocker. Cannot be called on a done task.
    /// </remarks>
    public record AssignTaskRequest(Guid AssigneeId);

    /// <summary>Assigns an engineer to a task that currently has none.</summary>
    /// <remarks>Anyone with access to the task may claim it for themselves; assigning it to
    /// someone else is Team Lead and above, and a plain Team Lead may only assign to an engineer
    /// on the team they lead (PM-or-above roles are unrestricted). Reassigning an already-assigned
    /// task, or moving work across departments, goes through the Edit form or Loan task instead.</remarks>
    [HttpPost("{id:guid}/assign")]
    [ProducesResponseType(typeof(ApiResponse<TaskDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> AssignTask(Guid id, [FromBody] AssignTaskRequest request) =>
        (await _mediator.Send(new AssignTaskCommand(id, request.AssigneeId, GetActorId(), GetIp(), GetRole()))).ToActionResult();

    /// <summary>Pulls an Active task back into the Backlog — unassigns it, clears its due date, and
    /// removes it from its sprint if it's in one.</summary>
    /// <remarks>Team Lead and above. A plain Team Lead may only return a task currently held by an
    /// engineer on the team they lead; PM-or-above roles are unrestricted. Only valid from Active —
    /// a blocked, paused, in-QA, or done task must be brought back to Active first.</remarks>
    [HttpPost("{id:guid}/return-to-backlog")]
    [RequiresCapability(CapabilityRegistry.TeamLeadOrAbove)]
    [ProducesResponseType(typeof(ApiResponse<TaskDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ReturnToBacklog(Guid id) =>
        (await _mediator.Send(new ReturnTaskToBacklogCommand(id, GetActorId(), GetIp(), GetRole()))).ToActionResult();

    [HttpPost("{id:guid}/blocker")]
    [ProducesResponseType(typeof(ApiResponse<TaskDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> FlagBlocker(Guid id, [FromBody] FlagBlockerRequest request) =>
        (await _mediator.Send(new FlagBlockerCommand(id, request.Reason, GetActorId(), GetIp(), GetRole()))).ToActionResult();

    /// <summary>Clears the blocker on a task, returning it to Active.</summary>
    [HttpDelete("{id:guid}/blocker")]
    [ProducesResponseType(typeof(ApiResponse<TaskDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ClearBlocker(Guid id) =>
        (await _mediator.Send(new ClearBlockerCommand(id, GetActorId(), GetIp(), GetRole()))).ToActionResult();

    /// <summary>Pauses a task — a voluntary hold, distinct from a blocker.</summary>
    /// <remarks>The assigned engineer, or anyone with access to the task's project (team lead, PM,
    /// department/PMO head), may pause it — mirrors Resume/ClearBlocker's access rule. Cannot be called
    /// on a task that isn't Active.</remarks>
    [HttpPost("{id:guid}/pause")]
    [ProducesResponseType(typeof(ApiResponse<TaskDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> PauseTask(Guid id, [FromBody] PauseTaskRequest request) =>
        (await _mediator.Send(new PauseTaskCommand(id, request.Note, GetActorId(), GetIp(), GetRole()))).ToActionResult();

    /// <summary>Resumes a paused task, returning it to Active.</summary>
    [HttpDelete("{id:guid}/pause")]
    [ProducesResponseType(typeof(ApiResponse<TaskDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ResumeTask(Guid id) =>
        (await _mediator.Send(new ResumeTaskCommand(id, GetActorId(), GetIp(), GetRole()))).ToActionResult();

    /// <summary>Marks a task done. Self-service alternative to the PM-only PATCH endpoint — the task's
    /// assignee can complete their own task (including accepting a QA sub-task) without PM+ access.</summary>
    [HttpPost("{id:guid}/mark-done")]
    [ProducesResponseType(typeof(ApiResponse<TaskDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> MarkTaskDone(Guid id) =>
        (await _mediator.Send(new UpdateTaskCommand(
            id,
            Title: null, Description: null, AcceptanceCriteria: null, Severity: null,
            EpicId: null, RemoveFromEpic: null, Points: null, DueDate: null, ActualEndDate: null,
            AssigneeId: null, Type: null, MarkDone: true, SprintId: null, RemoveFromSprint: null,
            Status: null, BlockerReason: null, PauseNote: null, RequiresQa: null, Discipline: null,
            ActorId: GetActorId(), IpAddress: GetIp(), ActorRole: GetRole()))).ToActionResult();

    /// <summary>Previews the overwork impact of assigning a task to an engineer, without committing the change.</summary>
    [HttpPost("{id:guid}/preview-assign")]
    [RequiresCapability(CapabilityRegistry.PmOrAbove)]
    [ProducesResponseType(typeof(ApiResponse<PreviewAssignResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> PreviewAssign(Guid id, [FromBody] PreviewAssignRequest request) =>
        (await _mediator.Send(new PreviewAssignCommand(id, request.TargetEngineerId))).ToActionResult();

    /// <summary>Atomically reassigns multiple tasks to a target engineer.</summary>
    /// <remarks>
    /// All tasks are moved in a single transaction. Returns the overwork signal state after reassignment.
    /// The operation proceeds even if it would flag overwork — the warning is surfaced in the response.
    /// </remarks>
    [HttpPost("bulk-reassign")]
    [RequiresCapability(CapabilityRegistry.PmOrAbove)]
    [ProducesResponseType(typeof(ApiResponse<BulkReassignResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> BulkReassign([FromBody] BulkReassignRequest request) =>
        (await _mediator.Send(new BulkReassignCommand(
            request.TaskIds, request.TargetEngineerId, GetActorId(), GetIp(), GetRole()))).ToActionResult();

    /// <summary>Bulk changes the status of a list of tasks to 'done' or 'active'. Returns count of tasks actually changed.</summary>
    [HttpPost("bulk-status")]
    [RequiresCapability(CapabilityRegistry.PmOrAbove)]
    [ProducesResponseType(typeof(ApiResponse<int>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> BulkStatusChange([FromBody] BulkStatusChangeRequest request) =>
        (await _mediator.Send(new BulkStatusChangeCommand(
            request.TaskIds, request.TargetStatus, GetActorId(), GetIp(), GetRole()))).ToActionResult();

    /// <summary>Bulk-imports up to 200 tasks into a project from a JSON array. Fails per-item; partial success is returned.</summary>
    [HttpPost("bulk")]
    [RequiresCapability(CapabilityRegistry.PmOrAbove)]
    [ProducesResponseType(typeof(ApiResponse<BulkCreateTasksResult>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> BulkCreateTasks([FromBody] BulkCreateTasksRequest request) =>
        (await _mediator.Send(new BulkCreateTasksCommand(
            request.ProjectId,
            request.Tasks.Select(t =>
            {
                var tt = Enum.TryParse<TaskType>(t.Type, ignoreCase: true, out var pt) ? pt : TaskType.Feature;
                return new BulkTaskItem(t.Title, t.Description, t.Points, t.DueDate, t.AssigneeId, tt, t.AcceptanceCriteria);
            }).ToList(),
            GetActorId(), GetIp(), GetRole()))).ToCreatedResult();

    /// <summary>Loans a task to an engineer in a different department.</summary>
    /// <remarks>
    /// Only the designated Team Lead of the task's current assignee's team may loan the task —
    /// department heads and PMO (Head of PMO / Project Manager) may loan any task, same as they
    /// aren't tied to a single team's lead role elsewhere in the app.
    /// The target engineer must be outside the actor's department. Resets the escalation clock for the new assignee.
    /// </remarks>
    [HttpPost("{id:guid}/loan")]
    [RequiresCapability(CapabilityRegistry.TeamLeadOrHeadOnly, CapabilityRegistry.PmoOnly)]
    [ProducesResponseType(typeof(ApiResponse<TaskDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> LoanTask(Guid id, [FromBody] LoanTaskRequest request) =>
        (await _mediator.Send(new LoanTaskCommand(id, request.TargetEngineerId, request.Reason, GetActorId(), GetIp(), GetRole()))).ToActionResult();

    /// <summary>Reverses a loan, handing a task back to whoever had it before.</summary>
    /// <remarks>
    /// The current holder (whoever has it on loan) may always recall it themselves; otherwise
    /// only the designated Team Lead of the team it was originally loaned from (or a department
    /// head, or PMO — Head of PMO / Project Manager) may. No role-based gate here — the handler
    /// is the sole authority, since it needs the task's current holder to decide self-recall
    /// eligibility, which a role-only attribute can't express. No-op target validation: fails if
    /// the task was never loaned, or if a later reassignment has already superseded that loan.
    /// </remarks>
    [HttpPost("{id:guid}/recall")]
    [ProducesResponseType(typeof(ApiResponse<TaskDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> RecallTask(Guid id) =>
        (await _mediator.Send(new RecallTaskCommand(id, GetActorId(), GetIp(), GetRole()))).ToActionResult();

    /// <summary>Returns tasks that block this task and tasks this task blocks.</summary>
    [HttpGet("{id:guid}/dependencies")]
    [ProducesResponseType(typeof(ApiResponse<TaskLinksDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDependencies(Guid id) =>
        (await _mediator.Send(new GetTaskDependenciesQuery(id, GetActorId(), GetRole()))).ToActionResult();

    public record AddDependencyRequest(Guid BlockingTaskId);

    /// <summary>Adds a dependency: this task is blocked until the specified blocking task is done.</summary>
    /// <remarks>The assignee of either task involved, or anyone with access to its project, may add
    /// the link — mirrors Pause/Resume's access rule.</remarks>
    [HttpPost("{id:guid}/dependencies")]
    [ProducesResponseType(typeof(ApiResponse<TaskDependencyDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> AddDependency(Guid id, [FromBody] AddDependencyRequest request) =>
        (await _mediator.Send(new AddTaskDependencyCommand(
            request.BlockingTaskId, id, GetActorId(), GetIp(), GetRole()))).ToActionResult();

    /// <summary>Removes a dependency link.</summary>
    /// <remarks>The dependent task's assignee, or anyone with access to its project, may remove it.</remarks>
    [HttpDelete("{id:guid}/dependencies/{blockingTaskId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> RemoveDependency(Guid id, Guid blockingTaskId)
    {
        var result = await _mediator.Send(new RemoveTaskDependencyCommand(blockingTaskId, id, GetActorId(), GetIp(), GetRole()));
        return result.IsSuccess ? NoContent() : result.ToActionResult();
    }

    /// <summary>Returns a task's checklist of subtasks.</summary>
    [HttpGet("{id:guid}/subtasks")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<SubtaskDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetSubtasks(Guid id) =>
        (await _mediator.Send(new GetSubtasksQuery(id, GetActorId(), GetRole()))).ToActionResult();

    public record AddSubtaskRequest(string Title);

    /// <summary>Adds a subtask (checklist item) to a task.</summary>
    /// <remarks>The task's assignee, or a team lead or above, may add subtasks.</remarks>
    [HttpPost("{id:guid}/subtasks")]
    [ProducesResponseType(typeof(ApiResponse<SubtaskDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> AddSubtask(Guid id, [FromBody] AddSubtaskRequest request) =>
        (await _mediator.Send(new AddSubtaskCommand(id, request.Title, GetActorId(), GetRole()))).ToCreatedResult();

    public record ToggleSubtaskRequest(bool IsDone);

    /// <summary>Marks a subtask done or not-done.</summary>
    /// <remarks>The task's assignee, or a team lead or above, may check off subtasks.</remarks>
    [HttpPatch("{id:guid}/subtasks/{subtaskId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<SubtaskDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ToggleSubtask(Guid id, Guid subtaskId, [FromBody] ToggleSubtaskRequest request) =>
        (await _mediator.Send(new ToggleSubtaskCommand(subtaskId, request.IsDone, GetActorId(), GetRole(), GetIp()))).ToActionResult();

    /// <summary>Deletes a subtask.</summary>
    /// <remarks>The task's assignee, or a team lead or above, may delete subtasks.</remarks>
    [HttpDelete("{id:guid}/subtasks/{subtaskId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DeleteSubtask(Guid id, Guid subtaskId)
    {
        var result = await _mediator.Send(new DeleteSubtaskCommand(subtaskId, GetActorId(), GetRole()));
        return result.IsSuccess ? NoContent() : result.ToActionResult();
    }

    public record LoanSubtaskRequest(Guid TargetEngineerId, string? Reason);

    /// <summary>Loans a subtask (checklist item) to an engineer in a different department.</summary>
    /// <remarks>Team lead (own team only) or department head/PMO. Mirrors the full-task Loan's
    /// rules — see LoanTask above.</remarks>
    [HttpPost("{id:guid}/subtasks/{subtaskId:guid}/loan")]
    [RequiresCapability(CapabilityRegistry.TeamLeadOrHeadOnly, CapabilityRegistry.PmoOnly)]
    [ProducesResponseType(typeof(ApiResponse<SubtaskDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> LoanSubtask(Guid id, Guid subtaskId, [FromBody] LoanSubtaskRequest request) =>
        (await _mediator.Send(new LoanSubtaskCommand(id, subtaskId, request.TargetEngineerId, request.Reason, GetActorId(), GetIp(), GetRole()))).ToActionResult();

    /// <summary>Reverses a subtask loan, pulling it back to unassigned.</summary>
    /// <remarks>Same eligibility as LoanSubtask, plus the subtask's current holder may always
    /// recall it themselves — no role-based gate here, since the handler needs the subtask's
    /// current assignee to decide that, which a role-only attribute can't express. A subtask has
    /// no prior named owner to restore (unlike a full task's Recall) — recalling always means
    /// clearing its assignee.</remarks>
    [HttpPost("{id:guid}/subtasks/{subtaskId:guid}/recall")]
    [ProducesResponseType(typeof(ApiResponse<SubtaskDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> RecallSubtask(Guid id, Guid subtaskId) =>
        (await _mediator.Send(new RecallSubtaskCommand(id, subtaskId, GetActorId(), GetIp(), GetRole()))).ToActionResult();

    private Guid GetActorId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string GetRole() => User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;
    private string? GetIp() => HttpContext.Connection.RemoteIpAddress?.ToString();
}
