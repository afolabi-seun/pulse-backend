using Pulse.Domain.Tasks;

namespace Pulse.Application.Tasks;

public record TaskDto(
    Guid Id,
    string Title,
    string? Description,
    string? AcceptanceCriteria,
    string? Severity,
    int Points,
    int TaskNumber,
    string? ExternalReference,
    DateOnly? DueDate,
    DateOnly? ActualEndDate,
    string Status,
    string TaskType,
    Guid ProjectId,
    Guid? EpicId,
    Guid? AssigneeId,
    Guid? SprintId,
    string? BlockerReason,
    bool RequiresQa,
    string? Discipline,
    bool RequiresFrontendHandoff,
    string? CurrentStage,
    Guid? ParentTaskId,
    Guid? QaTaskId,
    string? ReactivationReason,
    string? PauseNote,
    DateTime CreatedAt,
    DateTime ActivatedAt,
    string? ProjectName = null,
    string? AssigneeName = null,
    Guid? CreatedById = null,
    string? CreatorName = null,
    bool CanRejectQa = false,
    Guid? ReactivatedByEngineerId = null,
    string? ReactivatedByName = null,
    Guid? LoanedFromEngineerId = null,
    int? Priority = null,
    int? SubtasksDone = null,
    int? SubtasksTotal = null,
    DateTime? AssignedAt = null,
    string? ProjectCode = null,
    DateOnly? SentToQaAt = null,
    Guid? BackendAssigneeId = null,
    string? BackendAssigneeName = null,
    string? PendingRejectionReason = null,
    Guid? PendingRejectionActorId = null,
    string? PendingRejectionActorName = null,
    string? PendingRejectionResponse = null,
    Guid? PendingRejectionRespondedByEngineerId = null,
    string? PendingRejectionRespondedByName = null,
    bool RequiresPrApproval = false,
    string? PrLink = null,
    DateTime? PendingPrApprovalRequestedAt = null,
    string? PendingPrApprovalRequestedByName = null,
    string? PendingPrApprovalDelegatedToName = null,
    IReadOnlyList<string>? PendingPrApprovalApproverNames = null,
    bool CanApprovePrApproval = false,
    DateTime? PrApprovedAt = null,
    DueDateChangeDto? DueDateChange = null,
    PointsChangeDto? PointsChange = null,
    /// <summary>True when the task is in QA but its QA task does not exist, so nothing can accept it (task detail only).</summary>
    bool QaTaskMissing = false,
    // True for a task in its owner's personal-tasks project (see Project.PersonalOwnerId) — a private to-do,
    // so the UI leaves out what only makes sense for team delivery (estimates, hand-offs, blockers).
    bool IsPersonal = false)
{
    /// <summary>Display key combining the owning project's Code with this task's own TaskNumber
    /// (e.g. "NOTIF-011") — null when the caller didn't resolve ProjectCode (most action-command
    /// responses skip it; their result is immediately superseded by a refetch of the real list/detail
    /// query anyway, so it isn't worth a project lookup there).</summary>
    public string? TaskKey => ProjectCode is null ? null : $"{ProjectCode}-{TaskNumber}";

    public static TaskDto From(PulseTask t, string? projectName = null, string? assigneeName = null,
        string? creatorName = null, bool canRejectQa = false, string? reactivatedByName = null,
        int? subtasksDone = null, int? subtasksTotal = null, string? projectCode = null,
        string? backendAssigneeName = null, string? pendingRejectionActorName = null,
        string? pendingRejectionRespondedByName = null, string? pendingPrApprovalRequestedByName = null,
        string? pendingPrApprovalDelegatedToName = null, IReadOnlyList<string>? pendingPrApprovalApproverNames = null,
        bool canApprovePrApproval = false) => new(
        t.Id, t.Title, t.Description, t.AcceptanceCriteria, t.Severity?.ToString(),
        t.Points, t.TaskNumber, t.ExternalReference, t.DueDate, t.ActualEndDate,
        Camel(t.Status.ToString()), Camel(t.Type.ToString()), t.ProjectId, t.EpicId,
        t.AssigneeId, t.SprintId, t.BlockerReason,
        t.RequiresQa, t.Discipline.HasValue ? Camel(t.Discipline.Value.ToString()) : null,
        t.RequiresFrontendHandoff, t.CurrentStage.HasValue ? Camel(t.CurrentStage.Value.ToString()) : null,
        t.ParentTaskId, t.QaTaskId, t.ReactivationReason, t.PauseNote,
        t.CreatedAt, t.ActivatedAt, projectName, assigneeName, t.CreatedById, creatorName, canRejectQa,
        t.ReactivatedByEngineerId, reactivatedByName, t.LoanedFromEngineerId, t.Priority,
        subtasksDone, subtasksTotal,
        // The current assignee's own assignment event — every "assignee_id" TaskHistory entry
        // records a real assignment (Assign() is the only writer, always a concrete engineer), so
        // the most recent one's timestamp is exactly when the current assignee was assigned. Only
        // populated when History was eager-loaded (GetTaskQuery does; list/board queries don't
        // need it) — empty History resolves to null here rather than a stale/wrong value.
        t.History.Where(h => h.Field == "assignee_id").OrderByDescending(h => h.ChangedAt).FirstOrDefault()?.ChangedAt,
        projectCode, t.SentToQaAt, t.BackendAssigneeId, backendAssigneeName,
        t.PendingRejectionReason, t.PendingRejectionActorId, pendingRejectionActorName,
        t.PendingRejectionResponse, t.PendingRejectionRespondedByEngineerId, pendingRejectionRespondedByName,
        t.RequiresPrApproval, t.PrLink, t.PendingPrApprovalRequestedAt, pendingPrApprovalRequestedByName,
        pendingPrApprovalDelegatedToName, pendingPrApprovalApproverNames, canApprovePrApproval, t.PrApprovedAt);

    private static string Camel(string s) => s.Length == 0 ? s : char.ToLower(s[0]) + s[1..];
}

/// <summary>The most recent time a person changed this task's existing estimate, with the reason they gave.
/// Only populated on the task detail query.</summary>
public record PointsChangeDto(
    int? From,
    int? To,
    string Reason,
    DateTime ChangedAt,
    Guid ChangedById,
    string? ChangedByName);

/// <summary>The most recent time a person moved this task's existing due date, with the reason they
/// gave. Only populated on the task detail query.</summary>
public record DueDateChangeDto(
    DateOnly? From,
    DateOnly? To,
    string Reason,
    DateTime ChangedAt,
    Guid ChangedById,
    string? ChangedByName);
