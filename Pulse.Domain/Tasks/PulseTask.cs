using Pulse.Domain.Common;

namespace Pulse.Domain.Tasks;

public class PulseTask : Entity
{
    public string Title { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public string? AcceptanceCriteria { get; private set; }
    public BugSeverity? Severity { get; private set; }
    /// <summary>1 (lowest) to 5 (highest). Applies to every task type, unlike Severity which is
    /// bug-specific. Null means unprioritized.</summary>
    public int? Priority { get; private set; }
    public int Points { get; private set; }
    public DateOnly? DueDate { get; private set; }
    public TaskStatus Status { get; private set; } = TaskStatus.Backlog;
    public TaskType Type { get; private set; } = TaskType.Feature;
    public Guid ProjectId { get; private set; }
    public Guid? EpicId { get; private set; }
    public Guid? AssigneeId { get; private set; }
    public Guid? CreatedById { get; private set; }
    public Guid? SprintId { get; private set; }
    public string? BlockerReason { get; private set; }
    public bool RequiresQa { get; private set; }
    public Discipline? Discipline { get; private set; }
    /// <summary>Opt-in flag for a task split between a backend and a frontend developer before it's
    /// done (or sent to QA) — see <see cref="HandOffToFrontend"/>. Independent of RequiresQa/Discipline;
    /// a task can require both a frontend handoff and QA sign-off.</summary>
    public bool RequiresFrontendHandoff { get; private set; }
    /// <summary>Which half of a RequiresFrontendHandoff task is currently assigned out. Null for any
    /// task that isn't flagged. Purely informational — see <see cref="TaskStage"/>.</summary>
    public TaskStage? CurrentStage { get; private set; }
    /// <summary>The most recent engineer to hold the Backend stage of a RequiresFrontendHandoff
    /// task — kept even after <see cref="HandOffToFrontend"/> moves AssigneeId on to a frontend
    /// developer, so the backend engineer's finished work stays attributable to them (on the task
    /// detail page and their own task list) instead of disappearing the moment the task is
    /// reassigned. Null for a task that's never had a frontend handoff.</summary>
    public Guid? BackendAssigneeId { get; private set; }
    public Guid? ParentTaskId { get; private set; }
    public Guid? QaTaskId { get; private set; }
    public string? ReactivationReason { get; private set; }
    public Guid? ReactivatedByEngineerId { get; private set; }
    /// <summary>A QA rejection QA has proposed but not yet confirmed — the task stays InQa and
    /// nothing about it (status, ActualEndDate, the linked QA sub-task) changes until QA calls
    /// <see cref="ConfirmQaRejection"/> or <see cref="WithdrawQaRejection"/>. Lets the assignee
    /// respond first (e.g. "this is an environment issue") so a rejection that turns out not to
    /// be a real code problem never counts as an iteration. Null when nothing is pending.</summary>
    public string? PendingRejectionReason { get; private set; }
    /// <summary>The QA reviewer who proposed the pending rejection.</summary>
    public Guid? PendingRejectionActorId { get; private set; }
    /// <summary>The assignee's reply to a pending rejection — informational, not a gate: QA may
    /// confirm or withdraw whether or not a response has come in yet.</summary>
    public string? PendingRejectionResponse { get; private set; }
    public Guid? PendingRejectionRespondedByEngineerId { get; private set; }
    /// <summary>QA's opinion of which side of a RequiresFrontendHandoff task is actually
    /// responsible for a pending rejection — informational for the reverse direction (no stored
    /// frontend assignee to auto-route to), but when flagged Backend while the task is currently
    /// at the Frontend stage, ConfirmQaRejectionCommand auto-hands it back to BackendAssigneeId.
    /// Null when the task isn't a frontend-handoff task, or QA didn't specify. Cleared alongside
    /// the rest of the pending-rejection state.</summary>
    public TaskStage? PendingRejectionTargetStage { get; private set; }
    public bool RequiresPrApproval { get; private set; }
    public string? PrLink { get; private set; }
    /// <summary>Non-null while a PR-approval request is awaiting a decision. See <see cref="RequestPrApproval"/>.</summary>
    public DateTime? PendingPrApprovalRequestedAt { get; private set; }
    public Guid? PendingPrApprovalRequestedByEngineerId { get; private set; }
    /// <summary>Set only via <see cref="ReassignPrApprover"/> — overrides the default (the
    /// assignee's department head) while a request is pending, e.g. when that head is unavailable.</summary>
    public Guid? PendingPrApprovalDelegatedToEngineerId { get; private set; }
    /// <summary>The gate <see cref="MarkDone"/> checks when <see cref="RequiresPrApproval"/> is set.</summary>
    public DateTime? PrApprovedAt { get; private set; }
    public string? PauseNote { get; private set; }
    public DateOnly? ActualEndDate { get; private set; }
    /// <summary>When this task most recently entered QA — the point at which the assignee's own
    /// deadline is considered met under the current DoD (a task isn't formally closed until QA
    /// passes it, but QA's own turnaround shouldn't count as the assignee running late). Cleared
    /// on <see cref="ConfirmQaRejection"/> so a rejected-and-resubmitted task is judged on its next handoff,
    /// not its first. Null for a task with no QA step at all (see <see cref="MarkDone"/>), which
    /// keeps freezing lateness at <see cref="ActualEndDate"/> instead.</summary>
    public DateOnly? SentToQaAt { get; private set; }
    public DateTime ActivatedAt { get; private set; } = DateTime.UtcNow;
    public bool PausedByProject { get; private set; }
    public TaskStatus? StatusBeforePause { get; private set; }
    public DateTime? PausedAt { get; private set; }
    public Guid? LoanedFromEngineerId { get; private set; }
    /// <summary>Sequential, per-project display number — combined with the owning project's Code
    /// (e.g. "NOTIF") to form this task's display key, "NOTIF-011". 0 is a transient "not yet
    /// assigned" sentinel: every real creation path calls <see cref="AssignTaskNumber"/> immediately
    /// after <see cref="Create"/>, before the task is ever saved — kept off Create's own signature
    /// so existing callers (and DemoSeeder's many ad-hoc task-builder call sites) don't all need to
    /// resolve "the next number for this project" up front just to construct a task.</summary>
    public int TaskNumber { get; private set; }
    /// <summary>Optional free-text pointer to this task's ID in an external system (Jira, a legacy
    /// tracker, …) — deliberately not merged into TaskNumber/the generated key: that numbering is
    /// guaranteed unique and sequential within Pulse, which an imported foreign ID can't promise.</summary>
    public string? ExternalReference { get; private set; }

    private readonly List<TaskHistory> _history = [];
    public IReadOnlyList<TaskHistory> History => _history;

    private PulseTask() { }

    public static PulseTask Create(string title, int points, Guid projectId,
        TaskType type = TaskType.Feature, DateOnly? dueDate = null, Guid? createdById = null)
    {
        ValidatePoints(points);
        return new()
        {
            Title = title,
            Points = points,
            DueDate = dueDate,
            ProjectId = projectId,
            Type = type,
            CreatedById = createdById,
            ActivatedAt = DateTime.UtcNow
        };
    }

    /// <summary>Assigns this task's display number within its project — every real creation path
    /// calls this exactly once, immediately after <see cref="Create"/>. Not part of Create itself
    /// (see <see cref="TaskNumber"/>'s doc comment).</summary>
    public void AssignTaskNumber(int number)
    {
        if (TaskNumber != 0)
            throw new DomainException("Task number has already been assigned.");
        if (number < 1)
            throw new DomainException("Task number must be positive.");
        TaskNumber = number;
    }

    public void SetExternalReference(string? reference) => ExternalReference = reference;

    /// <summary>0 means ungroomed/not yet estimated (see AssignToSprint's doc comment — an
    /// unpointed task is still a valid, schedulable state); 1-13 is the story-point scale every
    /// caller is meant to offer. The single source of truth for that range: API request
    /// validators mirror it per endpoint for a fast 422 without a round trip, but this is what
    /// actually stops an out-of-range value reaching a task, including from paths that skip a
    /// validator entirely — the CSV/backlog importers, which parse their own "points" column.</summary>
    private static void ValidatePoints(int points)
    {
        if (points < 0 || points > 13)
            throw new DomainException("Points must be 0 (ungroomed) or between 1 and 13.");
    }

    public void Assign(Guid assigneeId, Guid actorId, string? context = null)
    {
        var old = AssigneeId?.ToString();
        AssigneeId = assigneeId;
        ActivatedAt = DateTime.UtcNow;
        // Any reassignment through the general path — not a loan or its recall — breaks the
        // recall lineage: recalling should never silently ship the task out from under whoever
        // a PM/lead deliberately moved it to next. Loan() and Recall() restore the field
        // themselves immediately after calling this.
        LoanedFromEngineerId = null;

        PromoteFromBacklogIfGroomed(actorId);

        _history.Add(TaskHistory.Record(Id, "assignee_id", old, assigneeId.ToString(), actorId, context));
    }

    /// <summary>Reassigns the task and remembers who had it, so <see cref="Recall"/> can undo it
    /// later. <paramref name="assigneeId"/> must already be known to be outside the current
    /// assignee's department — that comparison lives in LoanTaskHandler, which knows about teams;
    /// this method only knows about the assignee/history bookkeeping.</summary>
    public void Loan(Guid assigneeId, Guid actorId)
    {
        var previousAssigneeId = AssigneeId;
        Assign(assigneeId, actorId, context: "loaned");
        LoanedFromEngineerId = previousAssigneeId;
    }

    /// <summary>Reverses the most recent loan, handing the task back to whoever had it before.
    /// Throws if this task was never loaned, or if a later reassignment (regular or another loan)
    /// has already superseded that loan — <see cref="Assign"/> clears the field in both cases.</summary>
    public void Recall(Guid actorId)
    {
        if (LoanedFromEngineerId is null)
            throw new DomainException("This task is not currently on loan.");

        Assign(LoanedFromEngineerId.Value, actorId, context: "recalled");
    }

    /// <summary>A Backlog task only becomes real, scheduled work once it has both an assignee and
    /// has been pointed — checked here rather than left to individual callers, so a task imported
    /// ungroomed activates the moment it's actually complete, whichever of assignment or grooming
    /// happens second (see <see cref="Assign"/> and <see cref="UpdateDetails"/>). A due date is
    /// *not* required: it's nullable everywhere else in this domain (see
    /// <see cref="ResumeFromProjectHold"/>), so an open-ended task with no deadline is normal —
    /// only missing points signals the row genuinely isn't scoped yet.</summary>
    private void PromoteFromBacklogIfGroomed(Guid actorId)
    {
        if (Status != TaskStatus.Backlog || !AssigneeId.HasValue || Points < 1)
            return;

        // A due date set (or left) while the task sat ungroomed/unassigned in Backlog isn't the
        // assignee's doing — without this, a date that lapsed in Backlog would make the task read
        // as instantly overdue the moment it activates, with zero time to act. Push it to today
        // instead, mirroring ResumeFromProjectHold's own frozen-time fix. A due date still in the
        // future is untouched — only a lapsed one needs correcting.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (DueDate.HasValue && DueDate.Value < today)
        {
            var oldDueDate = DueDate;
            DueDate = today;
            _history.Add(TaskHistory.Record(Id, "due_date", oldDueDate?.ToString("O"), DueDate?.ToString("O"), actorId));
        }

        _history.Add(TaskHistory.Record(Id, "status", TaskStatus.Backlog.ToString(), TaskStatus.Active.ToString(), actorId));
        Status = TaskStatus.Active;
    }

    /// <summary>Moves a freshly created personal task (see Project.PersonalOwnerId) straight from Backlog to
    /// Active. A personal to-do has no estimate to wait for, and unlike an ordinary unpointed task it must
    /// be loggable and appear in the timer and time grid from the moment it exists — manual time entries
    /// reject a Backlog task. A no-op for anything not in Backlog.</summary>
    public void ActivateAsPersonal(Guid actorId)
    {
        if (Status != TaskStatus.Backlog) return;
        _history.Add(TaskHistory.Record(Id, "status", TaskStatus.Backlog.ToString(), TaskStatus.Active.ToString(), actorId));
        Status = TaskStatus.Active;
    }

    /// <summary>Called when a timer starts on this task. Points no longer gate this — a timer can
    /// run against an ungroomed Backlog task like any other action; it just won't auto-promote out
    /// of Backlog until it's actually pointed (see <see cref="PromoteFromBacklogIfGroomed"/>) — this
    /// is the mechanism the "Unclaimed" timer tab uses to claim a backlog item, so Backlog itself
    /// stays allowed here even though <see cref="EnsureCanLogTimeEntry"/> rejects it for a manual
    /// entry. Paused/Blocked ARE rejected, unlike before: a task deliberately suspended or stuck
    /// shouldn't start accruing new time just because someone opens a timer on it. Active is left
    /// alone, as always.</summary>
    public void ActivateForTimer(Guid actorId)
    {
        if (Status is TaskStatus.Done or TaskStatus.InQa)
            throw new DomainException("Cannot start a timer on a task that is done or awaiting QA.");
        if (Status == TaskStatus.Paused)
            throw new DomainException("Cannot start a timer on a paused task.");
        if (Status == TaskStatus.Blocked)
            throw new DomainException("Cannot start a timer on a blocked task.");

        PromoteFromBacklogIfGroomed(actorId);
    }

    /// <summary>Guards a manual time-entry against this task (LogTimeEntryCommand) — stricter than
    /// <see cref="ActivateForTimer"/>: Backlog is rejected here too, since there's no "claiming"
    /// motion to protect for a manual entry the way there is for the timer's Unclaimed tab. Done
    /// and InQa are deliberately NOT checked here — manual logging has never gated on them, and
    /// widening that is a separate decision from this one.</summary>
    public void EnsureCanLogTimeEntry()
    {
        if (Status == TaskStatus.Paused)
            throw new DomainException("Cannot log time on a paused task.");
        if (Status == TaskStatus.Blocked)
            throw new DomainException("Cannot log time on a blocked task.");
        if (Status == TaskStatus.Backlog)
            throw new DomainException("Cannot log time on a task that hasn't started yet.");
    }

    public void MarkDone(Guid actorId)
    {
        if (Status == TaskStatus.Done)
            throw new DomainException("Task is already done.");
        if (Status == TaskStatus.InQa)
            throw new DomainException("Task is awaiting QA. Accept or reject it from the linked QA task.");
        // A QA subtask can never itself require QA (SetRequiresQa enforces that), so this only
        // ever blocks the real gap: completing the original task directly (while still Active)
        // instead of going through SendToQa -> AcceptQa.
        if (RequiresQa)
            throw new DomainException("This task requires QA sign-off. Send it to QA first.");
        if (RequiresPrApproval && PrApprovedAt is null)
            throw new DomainException("This task requires PR approval sign-off. Request approval and get it approved first.");

        var old = Status.ToString();
        Status = TaskStatus.Done;
        ActualEndDate = DateOnly.FromDateTime(DateTime.UtcNow);
        _history.Add(TaskHistory.Record(Id, "status", old, TaskStatus.Done.ToString(), actorId, creditedEngineerId: LoanedFromEngineerId ?? BackendAssigneeId ?? AssigneeId));
    }

    /// <summary>Records a checklist item being completed — <see cref="Subtask"/> has no history of
    /// its own, so this lands on the parent task's history instead, which is what the project
    /// Activity feed already reads from. Only meant to be called for a genuine not-done -> done
    /// transition; un-checking a subtask is deliberately left unaudited (corrective noise, not a
    /// reportable event) — the caller decides which transitions qualify.</summary>
    public void RecordSubtaskCompleted(Guid actorId, string subtaskTitle) =>
        _history.Add(TaskHistory.Record(Id, "subtask_completed", null, subtaskTitle, actorId));

    public void SendToQa(Guid actorId)
    {
        if (ParentTaskId.HasValue)
            throw new DomainException("A QA task cannot itself be sent to QA.");
        if (Status == TaskStatus.Done)
            throw new DomainException("Task is already done.");
        if (Status == TaskStatus.InQa)
            throw new DomainException("Task is already awaiting QA.");
        if (!RequiresQa)
            throw new DomainException("Task is not flagged as requiring QA.");

        var old = Status.ToString();
        Status = TaskStatus.InQa;
        SentToQaAt = DateOnly.FromDateTime(DateTime.UtcNow);
        _history.Add(TaskHistory.Record(Id, "status", old, TaskStatus.InQa.ToString(), actorId));
    }

    /// <summary>The QA task this task was waiting on no longer exists (it was deleted, or the link never pointed at a real row), so nothing can
    /// accept or reject it and the task is stuck. Returns it to Active and drops the dead link, so it can be sent to QA again. Not a QA rejection:
    /// no reactivation reason is set and no iteration counts.</summary>
    public void ReturnFromQaWithoutQaTask(Guid actorId, string reason)
    {
        if (Status != TaskStatus.InQa)
            throw new DomainException("Task is not currently in QA.");

        var old = Status.ToString();
        var previousQaTaskId = QaTaskId;
        Status = TaskStatus.Active;
        SentToQaAt = null;
        QaTaskId = null;
        ClearPendingRejection();
        _history.Add(TaskHistory.Record(Id, "status", old, TaskStatus.Active.ToString(), actorId, reason: reason));
        if (previousQaTaskId.HasValue)
            _history.Add(TaskHistory.Record(Id, "qa_task_id", previousQaTaskId.Value.ToString(), null, actorId, reason: reason));
    }

    public void AcceptQa(Guid actorId)
    {
        if (Status != TaskStatus.InQa)
            throw new DomainException("Task is not currently in QA.");

        var old = Status.ToString();
        Status = TaskStatus.Done;
        ActualEndDate = DateOnly.FromDateTime(DateTime.UtcNow);
        ReactivationReason = null;
        ReactivatedByEngineerId = null;
        _history.Add(TaskHistory.Record(Id, "status", old, TaskStatus.Done.ToString(), actorId, creditedEngineerId: LoanedFromEngineerId ?? BackendAssigneeId ?? AssigneeId));
    }

    /// <summary>QA flags a problem with the review, but the task stays in QA — nothing about its
    /// status or timeline changes yet. See <see cref="PendingRejectionReason"/>.</summary>
    public void ProposeQaRejection(string reason, Guid actorId, TaskStage? targetStage = null)
    {
        if (Status != TaskStatus.InQa)
            throw new DomainException("Task is not currently in QA.");
        if (PendingRejectionReason is not null)
            throw new DomainException("A QA rejection is already pending a response on this task.");

        PendingRejectionReason = reason;
        PendingRejectionActorId = actorId;
        PendingRejectionTargetStage = targetStage;
        _history.Add(TaskHistory.Record(Id, "pending_rejection_reason", null, reason, actorId));
    }

    /// <summary>The assignee's reply to a pending rejection — purely informational, doesn't change
    /// whether or how QA can confirm or withdraw it.</summary>
    public void RespondToQaRejection(string response, Guid actorId)
    {
        if (PendingRejectionReason is null)
            throw new DomainException("There is no pending QA rejection to respond to.");

        PendingRejectionResponse = response;
        PendingRejectionRespondedByEngineerId = actorId;
        _history.Add(TaskHistory.Record(Id, "pending_rejection_response", null, response, actorId));
    }

    /// <summary>QA confirms the rejection stands — this is the real, historical "RejectQa" effect
    /// (and the point an iteration actually counts), just moved to whenever QA is satisfied rather
    /// than the moment they first raised a concern.</summary>
    public void ConfirmQaRejection(Guid actorId)
    {
        if (PendingRejectionReason is null)
            throw new DomainException("There is no pending QA rejection to confirm.");

        var old = Status.ToString();
        var reason = PendingRejectionReason;
        Status = TaskStatus.Active;
        ActualEndDate = null;
        SentToQaAt = null;
        ReactivationReason = reason;
        ReactivatedByEngineerId = actorId;
        ActivatedAt = DateTime.UtcNow;
        ClearPendingRejection();
        _history.Add(TaskHistory.Record(Id, "status", old, TaskStatus.Active.ToString(), actorId));
        _history.Add(TaskHistory.Record(Id, "reactivation_reason", null, reason, actorId));
    }

    /// <summary>QA is satisfied by the response (or reconsiders) and drops the rejection — the task
    /// stays exactly where it was in QA, with no iteration counted.</summary>
    public void WithdrawQaRejection(Guid actorId)
    {
        if (PendingRejectionReason is null)
            throw new DomainException("There is no pending QA rejection to withdraw.");

        ClearPendingRejection();
        _history.Add(TaskHistory.Record(Id, "pending_rejection_reason", "withdrawn", null, actorId));
    }

    private void ClearPendingRejection()
    {
        PendingRejectionReason = null;
        PendingRejectionActorId = null;
        PendingRejectionResponse = null;
        PendingRejectionRespondedByEngineerId = null;
        PendingRejectionTargetStage = null;
    }

    public void SetRequiresQa(bool requiresQa)
    {
        if (requiresQa && ParentTaskId.HasValue)
            throw new DomainException("A QA task cannot itself require QA sign-off.");
        RequiresQa = requiresQa;
    }

    public void SetRequiresPrApproval(bool requiresPrApproval)
    {
        if (requiresPrApproval && ParentTaskId.HasValue)
            throw new DomainException("A QA task cannot itself require PR approval.");
        RequiresPrApproval = requiresPrApproval;
    }

    public void RequestPrApproval(string prLink, Guid actorId)
    {
        if (!RequiresPrApproval)
            throw new DomainException("Task is not flagged as requiring PR approval.");
        if (Status == TaskStatus.Done)
            throw new DomainException("Task is already done.");
        if (PendingPrApprovalRequestedAt.HasValue)
            throw new DomainException("A PR approval request is already pending for this task.");

        PrLink = prLink;
        PendingPrApprovalRequestedAt = DateTime.UtcNow;
        PendingPrApprovalRequestedByEngineerId = actorId;
        _history.Add(TaskHistory.Record(Id, "pr_link", null, prLink, actorId));
    }

    public void ApprovePrApproval(Guid actorId)
    {
        if (!PendingPrApprovalRequestedAt.HasValue)
            throw new DomainException("No PR approval request is currently pending for this task.");

        PrApprovedAt = DateTime.UtcNow;
        ClearPendingPrApproval();
        _history.Add(TaskHistory.Record(Id, "pr_approved_at", null, PrApprovedAt.Value.ToString("O"), actorId));
    }

    /// <summary>Clears the pending request (including the submitted PrLink) so the requester
    /// resubmits with a fresh link — the rejection reason itself lives only in the notification
    /// sent to them, not as a persisted field, matching how FlagBlocker's reason isn't duplicated
    /// elsewhere once cleared.</summary>
    public void RejectPrApproval(Guid actorId)
    {
        if (!PendingPrApprovalRequestedAt.HasValue)
            throw new DomainException("No PR approval request is currently pending for this task.");

        PrLink = null;
        ClearPendingPrApproval();
        _history.Add(TaskHistory.Record(Id, "pr_link", "rejected", null, actorId));
    }

    /// <summary>Hands the pending decision to a specific engineer instead of the assignee's default
    /// department head — e.g. when that head is unavailable. Only meaningful while a request is
    /// pending; cleared the same way the rest of the pending state is once resolved.</summary>
    public void ReassignPrApprover(Guid newApproverId, Guid actorId)
    {
        if (!PendingPrApprovalRequestedAt.HasValue)
            throw new DomainException("No PR approval request is currently pending for this task.");

        var old = PendingPrApprovalDelegatedToEngineerId?.ToString();
        PendingPrApprovalDelegatedToEngineerId = newApproverId;
        _history.Add(TaskHistory.Record(Id, "pr_approval_delegated_to", old, newApproverId.ToString(), actorId));
    }

    private void ClearPendingPrApproval()
    {
        PendingPrApprovalRequestedAt = null;
        PendingPrApprovalRequestedByEngineerId = null;
        PendingPrApprovalDelegatedToEngineerId = null;
    }

    public void SetDiscipline(Discipline? discipline) => Discipline = discipline;

    /// <summary>Turning this on (re)starts the handoff at Backend; turning it off clears CurrentStage
    /// entirely, so the two fields never disagree about whether a handoff is in progress.</summary>
    public void SetRequiresFrontendHandoff(bool requiresFrontendHandoff)
    {
        if (requiresFrontendHandoff && ParentTaskId.HasValue)
            throw new DomainException("A QA task cannot itself require a frontend handoff.");
        RequiresFrontendHandoff = requiresFrontendHandoff;
        CurrentStage = requiresFrontendHandoff ? (CurrentStage ?? TaskStage.Backend) : null;
    }

    /// <summary>Reassigns a RequiresFrontendHandoff task from its backend developer to a frontend
    /// developer, without touching Status — the task stays wherever it already was (Active, Blocked,
    /// Paused), so escalation, board columns, and active-workload counting need no awareness of this
    /// at all. Once here, the frontend assignee's own MarkDone/SendToQa path is unmodified.</summary>
    public void HandOffToFrontend(Guid frontendAssigneeId, Guid actorId)
    {
        if (!RequiresFrontendHandoff)
            throw new DomainException("This task is not flagged for a frontend handoff.");
        if (CurrentStage != TaskStage.Backend)
            throw new DomainException("This task has already been handed off to Frontend.");
        if (Status is TaskStatus.Done or TaskStatus.InQa)
            throw new DomainException("Cannot hand off a task that is done or awaiting QA.");

        var old = AssigneeId?.ToString();
        BackendAssigneeId = AssigneeId;
        AssigneeId = frontendAssigneeId;
        CurrentStage = TaskStage.Frontend;
        LoanedFromEngineerId = null;
        _history.Add(TaskHistory.Record(Id, "assignee_id", old, frontendAssigneeId.ToString(), actorId));
        _history.Add(TaskHistory.Record(Id, "current_stage", TaskStage.Backend.ToString(), TaskStage.Frontend.ToString(), actorId));
    }

    /// <summary>The reverse of <see cref="HandOffToFrontend"/> — sends a task back to a backend
    /// developer, e.g. after picking the wrong frontend engineer or discovering more backend work
    /// is needed. Same shape: reassigns and reverts CurrentStage without touching Status.</summary>
    public void HandOffToBackend(Guid backendAssigneeId, Guid actorId)
    {
        if (!RequiresFrontendHandoff)
            throw new DomainException("This task is not flagged for a frontend handoff.");
        if (CurrentStage != TaskStage.Frontend)
            throw new DomainException("This task is not currently with Frontend.");
        if (Status is TaskStatus.Done or TaskStatus.InQa)
            throw new DomainException("Cannot hand off a task that is done or awaiting QA.");

        var old = AssigneeId?.ToString();
        AssigneeId = backendAssigneeId;
        BackendAssigneeId = backendAssigneeId;
        CurrentStage = TaskStage.Backend;
        LoanedFromEngineerId = null;
        _history.Add(TaskHistory.Record(Id, "assignee_id", old, backendAssigneeId.ToString(), actorId));
        _history.Add(TaskHistory.Record(Id, "current_stage", TaskStage.Frontend.ToString(), TaskStage.Backend.ToString(), actorId));
    }

    public void SetQaTaskId(Guid? qaTaskId) => QaTaskId = qaTaskId;

    public void SetParentTaskId(Guid parentTaskId) => ParentTaskId = parentTaskId;

    public void FlagBlocker(string reason, Guid actorId)
    {
        if (Status == TaskStatus.Done)
            throw new DomainException("Cannot flag a blocker on a done task.");

        var old = Status.ToString();
        Status = TaskStatus.Blocked;
        BlockerReason = reason;
        _history.Add(TaskHistory.Record(Id, "status", old, TaskStatus.Blocked.ToString(), actorId));
    }

    public void ClearBlocker(Guid actorId)
    {
        if (Status != TaskStatus.Blocked)
            throw new DomainException("Task is not blocked.");

        Status = TaskStatus.Active;
        BlockerReason = null;
        ActivatedAt = DateTime.UtcNow;
        _history.Add(TaskHistory.Record(Id, "status", TaskStatus.Blocked.ToString(), TaskStatus.Active.ToString(), actorId));
    }

    public void Pause(string? note, Guid actorId, bool byProject = false)
    {
        if (Status != TaskStatus.Active && Status != TaskStatus.Blocked)
            throw new DomainException("Only an active or blocked task can be paused.");

        var old = Status;
        StatusBeforePause = old;
        PausedAt = DateTime.UtcNow;
        PausedByProject = byProject;
        Status = TaskStatus.Paused;
        PauseNote = note;
        _history.Add(TaskHistory.Record(Id, "status", old.ToString(), TaskStatus.Paused.ToString(), actorId));
    }

    /// <summary>Voluntary, assignee-initiated resume. Rejects a task paused by a project hold — that one
    /// only comes back via <see cref="ResumeFromProjectHold"/>, so the project stays the single source of truth.</summary>
    public void Resume(Guid actorId)
    {
        if (Status != TaskStatus.Paused)
            throw new DomainException("Task is not paused.");
        if (PausedByProject)
            throw new DomainException("This task is paused because its project is on hold. Resume the project to reactivate it.");

        RestoreFromPause(actorId, shiftDueDate: false);
    }

    /// <summary>Resume path driven by the owning project coming off hold. Unlike a voluntary resume, this
    /// shifts the due date forward by however long the task sat paused — the delay wasn't the assignee's doing.</summary>
    public void ResumeFromProjectHold(Guid actorId)
    {
        if (Status != TaskStatus.Paused)
            throw new DomainException("Task is not paused.");
        if (!PausedByProject)
            throw new DomainException("This task was not paused by a project hold.");

        RestoreFromPause(actorId, shiftDueDate: true);
    }

    private void RestoreFromPause(Guid actorId, bool shiftDueDate)
    {
        var restoreTo = StatusBeforePause ?? TaskStatus.Active;

        if (shiftDueDate && DueDate.HasValue && PausedAt.HasValue)
        {
            var pausedDays = (DateTime.UtcNow.Date - PausedAt.Value.Date).Days;
            if (pausedDays > 0)
                DueDate = DueDate.Value.AddDays(pausedDays);
        }

        _history.Add(TaskHistory.Record(Id, "status", TaskStatus.Paused.ToString(), restoreTo.ToString(), actorId));

        Status = restoreTo;
        PauseNote = null;
        StatusBeforePause = null;
        PausedAt = null;
        PausedByProject = false;
        if (restoreTo == TaskStatus.Active)
            ActivatedAt = DateTime.UtcNow;
    }

    public void SetType(TaskType type, Guid actorId)
    {
        if (Type == type) return;
        _history.Add(TaskHistory.Record(Id, "type", Type.ToString(), type.ToString(), actorId));
        Type = type;
    }

    public void SetActualEndDate(DateOnly? date) => ActualEndDate = date;

    public void SeedDoneAt(Guid actorId, DateTime at)
    {
        Status = TaskStatus.Done;
        ActualEndDate = DateOnly.FromDateTime(at);
        _history.Add(TaskHistory.RecordAt(Id, "status", TaskStatus.Active.ToString(), TaskStatus.Done.ToString(), actorId, at, creditedEngineerId: LoanedFromEngineerId ?? BackendAssigneeId ?? AssigneeId));
    }

    public void SetSeverity(BugSeverity? severity) => Severity = severity;

    public void SetPriority(int? priority)
    {
        if (priority is < 1 or > 5)
            throw new DomainException("Priority must be between 1 and 5.");
        Priority = priority;
    }

    public void SetPoints(int points, Guid actorId, string? reason = null)
    {
        if (Status is TaskStatus.Done or TaskStatus.InQa)
            throw new DomainException("Cannot edit a task that is done or awaiting QA.");
        ValidatePoints(points);
        // Every change to a task's points is on the record: who, from what, to what, and (when one is given) why.
        if (Points != points)
            _history.Add(TaskHistory.Record(Id, "points", Points.ToString(), points.ToString(), actorId, reason: reason));
        Points = points;
        PromoteFromBacklogIfGroomed(actorId);
    }

    /// <summary>The reverse of <see cref="PromoteFromBacklogIfGroomed"/>'s auto-promotion —
    /// deliberately manual and narrow: only from Active (Blocked/InQa/Paused/Done each raise their
    /// own "is this even sensible" questions not worth solving here). Clears the assignee, due
    /// date, and sprint — matching Backlog's own invariant (see <see cref="TaskStatus.Backlog"/>'s
    /// doc comment: "no assignee yet") — a task can't sit in Backlog while still claiming to be
    /// someone's dated, in-sprint work. Points and everything else about the task are left alone.</summary>
    public void ReturnToBacklog(Guid actorId)
    {
        if (Status != TaskStatus.Active)
            throw new DomainException("Only an active task can be returned to the backlog.");

        var oldAssigneeId = AssigneeId?.ToString();
        var oldStatus = Status.ToString();

        Status = TaskStatus.Backlog;
        AssigneeId = null;
        DueDate = null;
        LoanedFromEngineerId = null;
        RemoveFromSprint();

        _history.Add(TaskHistory.Record(Id, "status", oldStatus, TaskStatus.Backlog.ToString(), actorId));
        if (oldAssigneeId is not null)
            _history.Add(TaskHistory.Record(Id, "assignee_id", oldAssigneeId, null, actorId));
    }

    public void AssignToEpic(Guid? epicId) => EpicId = epicId;

    /// <summary>Points are optional throughout the workflow (see PromoteFromBacklogIfGroomed for
    /// the one place grooming still gates something — auto-promotion out of Backlog); an unpointed
    /// task can still be scheduled into a sprint, its velocity math just won't count it.</summary>
    public void AssignToSprint(Guid sprintId)
    {
        SprintId = sprintId;
    }

    public void RemoveFromSprint() => SprintId = null;

    /// <summary>Shifts the due date by a number of days — used when the sprint a task belongs to has its
    /// own dates edited, so tasks keep their position relative to the sprint rather than being silently
    /// left with a due date outside the new range. A no-op for tasks with no due date, and for Done tasks
    /// (their due date is a closed record, same as <see cref="UpdateDetails"/>'s guard).</summary>
    public void ShiftDueDate(int days, Guid actorId, string? reason = null)
    {
        if (!DueDate.HasValue || days == 0 || Status == TaskStatus.Done) return;
        var old = DueDate;
        DueDate = DueDate.Value.AddDays(days);
        _history.Add(TaskHistory.Record(Id, "due_date", old?.ToString("O"), DueDate?.ToString("O"), actorId, reason: reason));
    }

    public void UpdateDetails(string title, string? description, string? acceptanceCriteria, int points, DateOnly? dueDate, Guid actorId, string? dueDateChangeReason = null, string? pointsChangeReason = null)
    {
        if (Status is TaskStatus.Done or TaskStatus.InQa)
            throw new DomainException("Cannot edit a task that is done or awaiting QA.");
        ValidatePoints(points);

        if (Title != title)
            _history.Add(TaskHistory.Record(Id, "title", Title, title, actorId));
        if (Points != points)
            _history.Add(TaskHistory.Record(Id, "points", Points.ToString(), points.ToString(), actorId, reason: pointsChangeReason));
        if (DueDate != dueDate)
            _history.Add(TaskHistory.Record(Id, "due_date", DueDate?.ToString("O"), dueDate?.ToString("O"), actorId, reason: dueDateChangeReason));

        Title = title;
        Description = description;
        AcceptanceCriteria = acceptanceCriteria;
        Points = points;
        DueDate = dueDate;

        PromoteFromBacklogIfGroomed(actorId);
    }
}
