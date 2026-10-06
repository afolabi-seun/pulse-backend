namespace Pulse.Domain.Notifications;

public static class NotificationKind
{
    public const string CheckInReminder    = "checkin_reminder";
    /// <summary>The Friday email reminding people who haven't logged hours this week. Email only.</summary>
    public const string TimeEntryReminder  = "time_entry_reminder";
    public const string EscalationT3      = "escalation_t3";
    public const string EscalationT1      = "escalation_t1";
    public const string EscalationOverdue = "escalation_overdue";
    public const string BlockerFlagged    = "blocker_flagged";
    public const string WeeklyVitalsPrompt  = "weekly_vitals_prompt";
    public const string WeeklyReportReady  = "weekly_report_ready";
    public const string PasswordReset     = "password_reset";
    public const string AccountLocked     = "account_locked";
    public const string QaRejected        = "qa_rejected";
    /// <summary>Sent to a task's assignee when QA proposes a rejection but hasn't confirmed it —
    /// the task stays in QA; a response can be submitted before it's confirmed as a real iteration.
    /// See ProposeQaRejectionCommand.</summary>
    public const string QaRejectionProposed = "qa_rejection_proposed";
    /// <summary>Sent to the QA reviewer who proposed a rejection once the assignee responds to it.
    /// See RespondToQaRejectionCommand.</summary>
    public const string QaRejectionResponded = "qa_rejection_responded";
    /// <summary>Sent to a task's assignee when QA withdraws a proposed rejection instead of
    /// confirming it — the task stays in QA, no iteration counted. See WithdrawQaRejectionCommand.</summary>
    public const string QaRejectionWithdrawn = "qa_rejection_withdrawn";
    public const string QaAccepted        = "qa_accepted";
    public const string QaUnassigned      = "qa_unassigned";
    public const string TaskUnblocked     = "task_unblocked";
    public const string TaskAssigned      = "task_assigned";
    public const string TaskLoaned        = "task_loaned";
    public const string TaskRecalled      = "task_recalled";
    public const string SubtaskLoaned     = "subtask_loaned";
    public const string SubtaskRecalled   = "subtask_recalled";
    public const string OverworkDigest    = "overwork_digest";
    public const string Mentioned         = "mentioned";
    public const string AlertRuleTriggered = "alert_rule_triggered";
    /// <summary>Sent to a task's previous assignee when AutomationRuleScanner reassigns it away from
    /// them for having sat Blocked too long — distinct from TaskAssigned, which goes to the new
    /// assignee (the team lead) instead.</summary>
    public const string AutomationTaskReassigned = "automation_task_reassigned";
    /// <summary>Sent to a task's previous assignee when a human (Team Lead or above) returns their
    /// Active task to the Backlog via ReturnTaskToBacklogCommand — a manual deprioritization, not
    /// the automation's own AutomationTaskReassigned kind.</summary>
    public const string TaskReturnedToBacklog = "task_returned_to_backlog";
    /// <summary>Sent to a task's assignee's department head(s) once a Team Lead+ submits a
    /// Planning-Poker estimate for approval. See SubmitEstimateForApprovalCommand.</summary>
    public const string EstimateApprovalRequested = "estimate_approval_requested";
    /// <summary>Sent to whoever submitted the estimate once a department head approves it and the
    /// points land on the task. See ApproveEstimateCommand.</summary>
    public const string EstimateApproved = "estimate_approved";
    /// <summary>Sent to whoever submitted the estimate once a department head rejects it — the
    /// session stays open (votes intact) for the team to resubmit. See RejectEstimateCommand.</summary>
    public const string EstimateRejected = "estimate_rejected";
    /// <summary>Sent to the department head(s) when a pending estimate's Team Lead grace period
    /// elapses with no action — the Team Lead keeps approval rights too, this is a backstop, not a
    /// handoff. See EstimateApprovalEscalationScanner.</summary>
    public const string EstimateApprovalEscalated = "estimate_approval_escalated";
    /// <summary>Sent to a feedback submitter when a department head replies privately — never
    /// visible to anyone else. See ReplyToFeedbackCommand.</summary>
    public const string FeedbackReplied = "feedback_replied";
    /// <summary>Sent to a task's assignee's department head(s) once a PR is submitted for approval.
    /// See RequestPrApprovalCommand.</summary>
    public const string PrApprovalRequested = "pr_approval_requested";
    /// <summary>Sent to whoever requested PR approval once it's approved and the task's PR-approval
    /// gate clears. See ApprovePrApprovalCommand.</summary>
    public const string PrApprovalApproved = "pr_approval_approved";
    /// <summary>Sent to whoever requested PR approval once it's rejected — the pending request and
    /// PR link are cleared, so they resubmit with a fresh link. See RejectPrApprovalCommand.</summary>
    public const string PrApprovalRejected = "pr_approval_rejected";
    /// <summary>Sent to the engineer a pending PR-approval request is explicitly handed to, e.g.
    /// because the assignee's department head is unavailable. See ReassignPrApproverCommand.</summary>
    public const string PrApprovalReassigned = "pr_approval_reassigned";
}
