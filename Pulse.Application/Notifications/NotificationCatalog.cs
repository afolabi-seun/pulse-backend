using Pulse.Domain.Notifications;

namespace Pulse.Application.Notifications;

/// <summary>One kind of notification as people see it in their preferences.</summary>
/// <param name="Mandatory">Security notices: their email can't be switched off.</param>
public record NotificationKindInfo(string Kind, string Category, string Label, string Description, bool Mandatory = false);

/// <summary>
/// Every notification kind, grouped and described for the notification preferences page. Email is on by
/// default for every kind — exactly what was sent before preferences existed — and a person can switch it off
/// per kind, except for <see cref="NotificationKindInfo.Mandatory"/> ones. The in-app inbox always records
/// every notification. A kind missing from here still sends, with email on.
/// </summary>
public static class NotificationCatalog
{
    public const string ProjectFollowStarted = "PROJECT_FOLLOW_STARTED";
    public const string ProjectFollowEnded = "PROJECT_FOLLOW_ENDED";

    public static readonly IReadOnlyList<NotificationKindInfo> All =
    [
        // Check-ins & surveys
        new(NotificationKind.CheckInReminder, "Check-ins & surveys", "Check-in reminder", "Morning reminder and evening nudge to submit your check-in"),
        new(NotificationKind.WeeklyVitalsPrompt, "Check-ins & surveys", "Weekly vitals survey", "Prompt to complete your Friday morale check-in"),
        new(NotificationKind.TimeEntryReminder, "Check-ins & surveys", "Time log reminder", "Friday reminder if you haven't logged any hours this week"),

        // Escalations
        new(NotificationKind.EscalationT3, "Escalations", "T-3 early warning", "A task is due in 3 or fewer days"),
        new(NotificationKind.EscalationT1, "Escalations", "T-1 off-ramp", "A task is due tomorrow"),
        new(NotificationKind.EscalationOverdue, "Escalations", "Overdue", "A task is past its due date"),

        // Tasks
        new(NotificationKind.TaskAssigned, "Tasks", "Task assigned", "A task was assigned to you"),
        new(NotificationKind.BlockerFlagged, "Tasks", "Blocker flagged", "A blocker was raised on a task you're involved in"),
        new(NotificationKind.TaskUnblocked, "Tasks", "Task unblocked", "A task you're involved in was unblocked"),
        new(NotificationKind.TaskLoaned, "Tasks", "Task loaned", "A task was loaned to you from another engineer"),
        new(NotificationKind.TaskRecalled, "Tasks", "Task recalled", "A task loaned to you was recalled"),
        new(NotificationKind.SubtaskLoaned, "Tasks", "Subtask loaned", "A subtask was loaned to you"),
        new(NotificationKind.SubtaskRecalled, "Tasks", "Subtask recalled", "A subtask loaned to you was recalled"),
        new(NotificationKind.TaskReturnedToBacklog, "Tasks", "Returned to backlog", "A task you were working on was returned to the backlog"),
        new(NotificationKind.AutomationTaskReassigned, "Tasks", "Reassigned by automation", "An automation rule reassigned one of your tasks"),

        // QA
        new(NotificationKind.QaRejectionProposed, "QA", "Rejection proposed", "QA proposed rejecting your task; you can respond"),
        new(NotificationKind.QaRejectionResponded, "QA", "Rejection responded to", "The assignee responded to a rejection you proposed"),
        new(NotificationKind.QaRejectionWithdrawn, "QA", "Rejection withdrawn", "QA withdrew a proposed rejection"),
        new(NotificationKind.QaRejected, "QA", "Task rejected", "QA rejected your task"),
        new(NotificationKind.QaAccepted, "QA", "Task accepted", "QA accepted your task"),
        new(NotificationKind.QaUnassigned, "QA", "QA unassigned", "A QA task has no reviewer"),

        // Pull requests
        new(NotificationKind.PrApprovalRequested, "Pull requests", "Approval requested", "Someone asked you to approve a pull request"),
        new(NotificationKind.PrApprovalApproved, "Pull requests", "Approved", "Your pull request was approved"),
        new(NotificationKind.PrApprovalRejected, "Pull requests", "Changes requested", "Your pull request was sent back"),
        new(NotificationKind.PrApprovalReassigned, "Pull requests", "Approver changed", "A pull request approval was reassigned"),

        // Estimation
        new(NotificationKind.EstimateApprovalRequested, "Estimation", "Estimate approval requested", "An estimate is waiting for your approval"),
        new(NotificationKind.EstimateApproved, "Estimation", "Estimate approved", "Your estimate was approved"),
        new(NotificationKind.EstimateRejected, "Estimation", "Estimate rejected", "Your estimate was sent back"),
        new(NotificationKind.EstimateApprovalEscalated, "Estimation", "Estimate approval escalated", "An estimate waited too long for approval"),

        // Collaboration
        new(NotificationKind.Mentioned, "Collaboration", "Mentioned", "Someone mentioned you in a comment"),
        new(NotificationKind.FeedbackReplied, "Collaboration", "Feedback replied", "Someone replied to feedback you sent"),
        new(ProjectFollowStarted, "Collaboration", "Project followed", "A department head started following a project your team works on"),
        new(ProjectFollowEnded, "Collaboration", "Project unfollowed", "A department head stopped following a project your team works on"),

        // Leadership
        new(NotificationKind.OverworkDigest, "Leadership", "Overwork digest", "Daily summary of engineers showing overwork signals"),
        new(NotificationKind.WeeklyReportReady, "Leadership", "Report ready", "The weekly leadership report is ready"),
        new(NotificationKind.AlertRuleTriggered, "Leadership", "Alert triggered", "One of your alert rules fired"),

        // Security
        new(NotificationKind.PasswordReset, "Security", "Password reset", "A link to reset your password", Mandatory: true),
        new(NotificationKind.AccountLocked, "Security", "Account locked", "Your account was locked after failed sign-ins", Mandatory: true),
    ];

    private static readonly Dictionary<string, NotificationKindInfo> ByKind = All.ToDictionary(k => k.Kind);

    public static NotificationKindInfo? Find(string kind) => ByKind.GetValueOrDefault(kind);

    public static bool IsMandatory(string kind) => Find(kind)?.Mandatory ?? false;
}
