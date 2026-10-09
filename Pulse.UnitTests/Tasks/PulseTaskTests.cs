using Pulse.Domain.Common;
using Pulse.Domain.Tasks;
using FluentAssertions;
using DomainTaskStatus = Pulse.Domain.Tasks.TaskStatus;

namespace Pulse.UnitTests.Tasks;

public class PulseTaskTests
{
    private static PulseTask NewTask(int points = 3, int dueDaysFromNow = 10) =>
        PulseTask.Create("Test task", points, Guid.NewGuid(),
            dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(dueDaysFromNow)));

    // ── Create ────────────────────────────────────────────────────────────────

    [Fact]
    public void Create_sets_title_and_points()
    {
        var projectId = Guid.NewGuid();
        var dueDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7));

        var task = PulseTask.Create("My task", 5, projectId, dueDate: dueDate);

        task.Title.Should().Be("My task");
        task.Points.Should().Be(5);
        task.DueDate.Should().Be(dueDate);
        task.ProjectId.Should().Be(projectId);
    }

    [Fact]
    public void Create_sets_status_to_Backlog_and_type_to_Feature()
    {
        // No assignee yet — not real work in progress until someone is assigned.
        var task = NewTask();

        task.Status.Should().Be(DomainTaskStatus.Backlog);
        task.Type.Should().Be(TaskType.Feature);
    }

    [Fact]
    public void Assign_transitions_a_Backlog_task_to_Active()
    {
        var task = NewTask();

        task.Assign(Guid.NewGuid(), Guid.NewGuid());

        task.Status.Should().Be(DomainTaskStatus.Active);
    }

    [Fact]
    public void Assign_leaves_an_ungroomed_task_in_Backlog()
    {
        // No points, no due date — e.g. an imported row too incomplete to be real work yet.
        var task = PulseTask.Create("Untriaged", 0, Guid.NewGuid());

        task.Assign(Guid.NewGuid(), Guid.NewGuid());

        task.Status.Should().Be(DomainTaskStatus.Backlog);
        task.AssigneeId.Should().NotBeNull();
    }

    [Fact]
    public void UpdateDetails_promotes_an_assigned_ungroomed_task_to_Active_once_complete()
    {
        var task = PulseTask.Create("Untriaged", 0, Guid.NewGuid());
        var actorId = Guid.NewGuid();
        task.Assign(Guid.NewGuid(), actorId);
        task.Status.Should().Be(DomainTaskStatus.Backlog); // still ungroomed

        task.UpdateDetails(task.Title, task.Description, task.AcceptanceCriteria,
            points: 5, dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)), actorId);

        task.Status.Should().Be(DomainTaskStatus.Active);
    }

    [Fact]
    public void Assign_activates_a_pointed_task_even_with_no_due_date()
    {
        // A due date isn't required for real work elsewhere in this domain (an existing Active
        // task can have its due date cleared — see ResumeFromProjectHold), so points alone,
        // not points-and-due-date, should be the grooming gate.
        var task = PulseTask.Create("Open-ended task", 3, Guid.NewGuid());

        task.Assign(Guid.NewGuid(), Guid.NewGuid());

        task.Status.Should().Be(DomainTaskStatus.Active);
        task.DueDate.Should().BeNull();
    }

    [Fact]
    public void UpdateDetails_does_not_promote_an_unassigned_Backlog_task()
    {
        var task = PulseTask.Create("Untriaged", 0, Guid.NewGuid());

        task.UpdateDetails(task.Title, task.Description, task.AcceptanceCriteria,
            points: 5, dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)), Guid.NewGuid());

        task.Status.Should().Be(DomainTaskStatus.Backlog);
    }

    [Fact]
    public void SetPoints_promotes_an_assigned_ungroomed_task_to_Active_once_pointed()
    {
        // Mirrors the "Accept Estimate" flow (planning poker) — likely the most common way an
        // imported ungroomed-but-assigned task actually gets groomed in practice.
        var task = PulseTask.Create("Untriaged", 0, Guid.NewGuid());
        var actorId = Guid.NewGuid();
        task.Assign(Guid.NewGuid(), actorId);
        task.Status.Should().Be(DomainTaskStatus.Backlog);

        task.SetPoints(5, actorId);

        task.Status.Should().Be(DomainTaskStatus.Active);
    }

    // ── Points bounds ─────────────────────────────────────────────────────────
    // 0 (ungroomed) or 1-13 (the story-point scale) — enforced here as the single source of
    // truth, not just by each API request validator, so no caller (including the CSV/backlog
    // importers, which parse their own "points" column) can slip an out-of-range value through.

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(13)]
    public void Create_accepts_points_at_the_edges_of_the_valid_range(int points) =>
        PulseTask.Create("Task", points, Guid.NewGuid()).Points.Should().Be(points);

    [Theory]
    [InlineData(-1)]
    [InlineData(14)]
    [InlineData(500)]
    public void Create_rejects_out_of_range_points(int points)
    {
        var act = () => PulseTask.Create("Task", points, Guid.NewGuid());

        act.Should().Throw<DomainException>().WithMessage("*0*13*");
    }

    [Fact]
    public void SetPoints_rejects_a_value_above_13()
    {
        var task = NewTask();

        var act = () => task.SetPoints(14, Guid.NewGuid());

        act.Should().Throw<DomainException>().WithMessage("*0*13*");
    }

    [Fact]
    public void UpdateDetails_rejects_a_value_above_13()
    {
        var task = NewTask();

        var act = () => task.UpdateDetails(task.Title, null, null, 14, task.DueDate, Guid.NewGuid());

        act.Should().Throw<DomainException>().WithMessage("*0*13*");
    }

    [Fact]
    public void Create_sets_ActivatedAt_close_to_now()
    {
        var before = DateTime.UtcNow;
        var task = NewTask();
        var after = DateTime.UtcNow;

        task.ActivatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void Create_with_explicit_type_stores_type()
    {
        var task = PulseTask.Create("Bug", 2, Guid.NewGuid(), TaskType.Bug,
            DateOnly.FromDateTime(DateTime.UtcNow.AddDays(5)));

        task.Type.Should().Be(TaskType.Bug);
    }

    // ── Assign ────────────────────────────────────────────────────────────────

    [Fact]
    public void Assign_sets_assignee()
    {
        var task = NewTask();
        var engineerId = Guid.NewGuid();

        task.Assign(engineerId, Guid.NewGuid());

        task.AssigneeId.Should().Be(engineerId);
    }

    [Fact]
    public void Assign_resets_ActivatedAt()
    {
        var task = NewTask();
        // Simulate time has passed since creation
        var oldActivatedAt = DateTime.UtcNow.AddDays(-5);
        typeof(PulseTask).GetProperty("ActivatedAt")!.SetValue(task, oldActivatedAt);

        task.Assign(Guid.NewGuid(), Guid.NewGuid());

        task.ActivatedAt.Should().BeAfter(oldActivatedAt);
    }

    [Fact]
    public void Assign_adds_history_entry()
    {
        // A fresh task starts in Backlog, so its first assignment also logs the
        // Backlog -> Active transition alongside the assignee change.
        var task = NewTask();

        task.Assign(Guid.NewGuid(), Guid.NewGuid());

        task.History.Should().HaveCount(2);
        task.History[0].Field.Should().Be("status");
        task.History[1].Field.Should().Be("assignee_id");
    }

    [Fact]
    public void Assign_on_an_already_active_task_does_not_add_a_status_history_entry()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid()); // Backlog -> Active, first assignment

        task.Assign(Guid.NewGuid(), Guid.NewGuid()); // reassignment — already Active

        task.History.Should().HaveCount(3);
        task.History[2].Field.Should().Be("assignee_id");
    }

    // ── Loan / Recall ─────────────────────────────────────────────────────────

    [Fact]
    public void Loan_remembers_the_previous_assignee()
    {
        var task = NewTask();
        var original = Guid.NewGuid();
        task.Assign(original, Guid.NewGuid());

        task.Loan(Guid.NewGuid(), Guid.NewGuid());

        task.LoanedFromEngineerId.Should().Be(original);
    }

    [Fact]
    public void Loan_reassigns_the_task()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        var borrower = Guid.NewGuid();

        task.Loan(borrower, Guid.NewGuid());

        task.AssigneeId.Should().Be(borrower);
    }

    [Fact]
    public void Recall_hands_the_task_back_to_the_original_assignee()
    {
        var task = NewTask();
        var original = Guid.NewGuid();
        task.Assign(original, Guid.NewGuid());
        task.Loan(Guid.NewGuid(), Guid.NewGuid());

        task.Recall(Guid.NewGuid());

        task.AssigneeId.Should().Be(original);
        task.LoanedFromEngineerId.Should().BeNull("a completed recall has nothing left to reverse");
    }

    [Fact]
    public void Recall_throws_when_the_task_was_never_loaned()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());

        var act = () => task.Recall(Guid.NewGuid());

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void A_regular_reassignment_clears_the_loan_so_it_can_no_longer_be_recalled()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        task.Loan(Guid.NewGuid(), Guid.NewGuid());

        // A PM/lead reassigns it onward through the regular path before anyone recalls it.
        task.Assign(Guid.NewGuid(), Guid.NewGuid());

        task.LoanedFromEngineerId.Should().BeNull();
        var act = () => task.Recall(Guid.NewGuid());
        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Loaning_a_task_a_second_time_tracks_only_the_most_recent_hop()
    {
        var task = NewTask();
        var firstOwner = Guid.NewGuid();
        task.Assign(firstOwner, Guid.NewGuid());
        var secondOwner = Guid.NewGuid();
        task.Loan(secondOwner, Guid.NewGuid());

        task.Loan(Guid.NewGuid(), Guid.NewGuid());

        task.LoanedFromEngineerId.Should().Be(secondOwner, "recall should undo only the latest loan, not skip past it");
    }

    [Fact]
    public void Loan_records_a_loaned_context_on_the_assignee_history_entry()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());

        task.Loan(Guid.NewGuid(), Guid.NewGuid());

        task.History.Last().Field.Should().Be("assignee_id");
        task.History.Last().Context.Should().Be("loaned");
    }

    [Fact]
    public void Recall_records_a_recalled_context_on_the_assignee_history_entry()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        task.Loan(Guid.NewGuid(), Guid.NewGuid());

        task.Recall(Guid.NewGuid());

        task.History.Last().Field.Should().Be("assignee_id");
        task.History.Last().Context.Should().Be("recalled");
    }

    [Fact]
    public void A_regular_reassignment_leaves_no_context_on_the_assignee_history_entry()
    {
        var task = NewTask();

        task.Assign(Guid.NewGuid(), Guid.NewGuid());

        task.History.Last().Context.Should().BeNull();
    }

    // ── MarkDone ──────────────────────────────────────────────────────────────

    [Fact]
    public void MarkDone_transitions_to_Done()
    {
        var task = NewTask();

        task.MarkDone(Guid.NewGuid());

        task.Status.Should().Be(DomainTaskStatus.Done);
    }

    [Fact]
    public void MarkDone_adds_history_entry()
    {
        var task = NewTask();

        task.MarkDone(Guid.NewGuid());

        task.History.Should().HaveCount(1);
        task.History[0].Field.Should().Be("status");
        task.History[0].NewValue.Should().Be(DomainTaskStatus.Done.ToString());
    }

    [Fact]
    public void MarkDone_credits_the_current_assignee_when_the_task_is_not_on_loan()
    {
        var task = NewTask();
        var assignee = Guid.NewGuid();
        task.Assign(assignee, Guid.NewGuid());

        task.MarkDone(Guid.NewGuid());

        task.History.Last().CreditedEngineerId.Should().Be(assignee);
    }

    [Fact]
    public void MarkDone_credits_the_lending_engineer_when_the_task_is_still_on_loan()
    {
        var task = NewTask();
        var lender = Guid.NewGuid();
        task.Assign(lender, Guid.NewGuid());
        task.Loan(Guid.NewGuid(), Guid.NewGuid());

        task.MarkDone(Guid.NewGuid());

        task.History.Last().CreditedEngineerId.Should().Be(lender,
            "the lending engineer's team committed the work; crediting the borrower would hide that it was ever lent out");
    }

    // ── RecordSubtaskCompleted ───────────────────────────────────────────────

    [Fact]
    public void RecordSubtaskCompleted_adds_a_subtask_completed_history_entry_with_the_title()
    {
        var task = NewTask();
        var actorId = Guid.NewGuid();

        task.RecordSubtaskCompleted(actorId, "Write the migration");

        var entry = task.History.Last();
        entry.Field.Should().Be("subtask_completed");
        entry.NewValue.Should().Be("Write the migration");
        entry.OldValue.Should().BeNull();
        entry.ActorId.Should().Be(actorId);
    }

    [Fact]
    public void MarkDone_twice_throws_DomainException()
    {
        var task = NewTask();
        task.MarkDone(Guid.NewGuid());

        var act = () => task.MarkDone(Guid.NewGuid());

        act.Should().Throw<DomainException>().WithMessage("*already done*");
    }

    [Fact]
    public void MarkDone_succeeds_for_an_unpointed_task()
    {
        // Points are optional throughout the workflow now — only backlog auto-promotion
        // (PromoteFromBacklogIfGroomed) still gates on grooming.
        var task = PulseTask.Create("Untriaged", 0, Guid.NewGuid());

        task.MarkDone(Guid.NewGuid());

        task.Status.Should().Be(DomainTaskStatus.Done);
    }

    [Fact]
    public void MarkDone_throws_when_the_task_requires_QA_and_was_never_sent()
    {
        // The real gap this guards: completing a RequiresQa task directly instead of going
        // through SendToQa -> AcceptQa. The frontend hides the "Mark done" button in this case,
        // but the API had no guard of its own.
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        task.SetRequiresQa(true);

        var act = () => task.MarkDone(Guid.NewGuid());

        act.Should().Throw<DomainException>().WithMessage("*requires QA sign-off*");
        task.Status.Should().Be(DomainTaskStatus.Active);
    }

    [Fact]
    public void MarkDone_throws_when_PR_approval_is_required_and_not_yet_approved()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        task.SetRequiresPrApproval(true);

        var act = () => task.MarkDone(Guid.NewGuid());

        act.Should().Throw<DomainException>().WithMessage("*requires PR approval*");
        task.Status.Should().Be(DomainTaskStatus.Active);
    }

    [Fact]
    public void MarkDone_succeeds_once_PR_approval_is_granted()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        task.SetRequiresPrApproval(true);
        task.RequestPrApproval("https://bitbucket.org/org/repo/pull-requests/1", Guid.NewGuid());
        task.ApprovePrApproval(Guid.NewGuid());

        task.MarkDone(Guid.NewGuid());

        task.Status.Should().Be(DomainTaskStatus.Done);
    }

    // ── ActivateForTimer ──────────────────────────────────────────────────────

    [Fact]
    public void ActivateForTimer_throws_for_a_Done_task()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        task.MarkDone(Guid.NewGuid());

        var act = () => task.ActivateForTimer(Guid.NewGuid());

        act.Should().Throw<DomainException>().WithMessage("*done or awaiting QA*");
    }

    [Fact]
    public void ActivateForTimer_throws_for_a_task_InQa()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        task.SetRequiresQa(true);
        task.SendToQa(Guid.NewGuid());

        var act = () => task.ActivateForTimer(Guid.NewGuid());

        act.Should().Throw<DomainException>().WithMessage("*done or awaiting QA*");
    }

    [Fact]
    public void ActivateForTimer_succeeds_for_an_ungroomed_Backlog_task_but_leaves_it_in_Backlog()
    {
        // Points no longer gate starting a timer, but PromoteFromBacklogIfGroomed still requires
        // them to actually leave Backlog — so this succeeds without throwing, and the task simply
        // stays in Backlog while the timer runs.
        var task = PulseTask.Create("Untriaged", 0, Guid.NewGuid());
        task.Assign(Guid.NewGuid(), Guid.NewGuid());

        task.ActivateForTimer(Guid.NewGuid());

        task.Status.Should().Be(DomainTaskStatus.Backlog);
    }

    [Fact]
    public void ActivateForTimer_on_a_groomed_but_unassigned_Backlog_task_does_not_assign_it()
    {
        // Points alone don't promote a task — PromoteFromBacklogIfGroomed also requires an
        // assignee. ActivateForTimer assumes the caller (StartTimerCommand) resolves assignment
        // first, so calling it directly on an unassigned task is a no-op, not an auto-claim.
        var task = NewTask();

        task.ActivateForTimer(Guid.NewGuid());

        task.Status.Should().Be(DomainTaskStatus.Backlog);
        task.AssigneeId.Should().BeNull();
    }

    [Fact]
    public void ActivateForTimer_throws_for_a_Blocked_task()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        task.FlagBlocker("waiting on design", Guid.NewGuid());

        var act = () => task.ActivateForTimer(Guid.NewGuid());

        act.Should().Throw<DomainException>().WithMessage("*blocked task*");
        task.Status.Should().Be(DomainTaskStatus.Blocked);
    }

    [Fact]
    public void ActivateForTimer_throws_for_a_Paused_task()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        task.Pause("stepping away", Guid.NewGuid());

        var act = () => task.ActivateForTimer(Guid.NewGuid());

        act.Should().Throw<DomainException>().WithMessage("*paused task*");
        task.Status.Should().Be(DomainTaskStatus.Paused);
    }

    [Fact]
    public void ActivateForTimer_leaves_Active_alone()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());

        task.ActivateForTimer(Guid.NewGuid());

        task.Status.Should().Be(DomainTaskStatus.Active);
    }

    [Fact]
    public void ActivateForTimer_still_succeeds_for_an_ungroomed_Backlog_task()
    {
        // Unlike Paused/Blocked, Backlog is deliberately left allowed here — starting a timer on
        // an unclaimed backlog task is the actual claiming mechanism (the "Unclaimed" timer tab),
        // not something EnsureCanLogTimeEntry's stricter Backlog rejection should touch.
        var task = PulseTask.Create("Untriaged", 0, Guid.NewGuid());
        task.Assign(Guid.NewGuid(), Guid.NewGuid());

        task.ActivateForTimer(Guid.NewGuid());

        task.Status.Should().Be(DomainTaskStatus.Backlog);
    }

    // ── EnsureCanLogTimeEntry ─────────────────────────────────────────────────

    [Fact]
    public void EnsureCanLogTimeEntry_throws_for_a_Paused_task()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        task.Pause("stepping away", Guid.NewGuid());

        var act = () => task.EnsureCanLogTimeEntry();

        act.Should().Throw<DomainException>().WithMessage("*paused task*");
    }

    [Fact]
    public void EnsureCanLogTimeEntry_throws_for_a_Blocked_task()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        task.FlagBlocker("waiting on design", Guid.NewGuid());

        var act = () => task.EnsureCanLogTimeEntry();

        act.Should().Throw<DomainException>().WithMessage("*blocked task*");
    }

    [Fact]
    public void EnsureCanLogTimeEntry_throws_for_a_Backlog_task()
    {
        // Stricter than ActivateForTimer — no "claiming" motion to protect for a manual entry.
        var task = NewTask();

        var act = () => task.EnsureCanLogTimeEntry();

        act.Should().Throw<DomainException>().WithMessage("*hasn't started*");
    }

    [Fact]
    public void EnsureCanLogTimeEntry_allows_an_Active_task()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());

        var act = () => task.EnsureCanLogTimeEntry();

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureCanLogTimeEntry_allows_a_Done_task()
    {
        // Deliberately unguarded here — manual logging has never gated on Done/InQa, and widening
        // that is a separate decision from this task.
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        task.MarkDone(Guid.NewGuid());

        var act = () => task.EnsureCanLogTimeEntry();

        act.Should().NotThrow();
    }

    // ── FlagBlocker ───────────────────────────────────────────────────────────

    [Fact]
    public void FlagBlocker_sets_status_to_Blocked_and_reason()
    {
        var task = NewTask();

        task.FlagBlocker("Waiting on API", Guid.NewGuid());

        task.Status.Should().Be(DomainTaskStatus.Blocked);
        task.BlockerReason.Should().Be("Waiting on API");
    }

    [Fact]
    public void FlagBlocker_adds_history_entry()
    {
        var task = NewTask();

        task.FlagBlocker("reason", Guid.NewGuid());

        task.History.Should().HaveCount(1);
        task.History[0].NewValue.Should().Be(DomainTaskStatus.Blocked.ToString());
    }

    [Fact]
    public void FlagBlocker_records_the_actual_prior_status_not_a_hardcoded_Active()
    {
        // A fresh task is Backlog, not Active — the history must reflect that.
        var task = PulseTask.Create("Untriaged", 3, Guid.NewGuid());
        task.Status.Should().Be(DomainTaskStatus.Backlog);

        task.FlagBlocker("reason", Guid.NewGuid());

        task.History[0].OldValue.Should().Be(DomainTaskStatus.Backlog.ToString());
    }

    [Fact]
    public void FlagBlocker_on_Done_task_throws_DomainException()
    {
        var task = NewTask();
        task.MarkDone(Guid.NewGuid());

        var act = () => task.FlagBlocker("reason", Guid.NewGuid());

        act.Should().Throw<DomainException>().WithMessage("*done*");
    }

    // ── ClearBlocker ──────────────────────────────────────────────────────────

    [Fact]
    public void ClearBlocker_sets_status_to_Active_and_clears_reason()
    {
        var task = NewTask();
        task.FlagBlocker("waiting", Guid.NewGuid());

        task.ClearBlocker(Guid.NewGuid());

        task.Status.Should().Be(DomainTaskStatus.Active);
        task.BlockerReason.Should().BeNull();
    }

    [Fact]
    public void ClearBlocker_resets_ActivatedAt()
    {
        var task = NewTask();
        task.FlagBlocker("waiting", Guid.NewGuid());
        var before = DateTime.UtcNow;

        task.ClearBlocker(Guid.NewGuid());

        task.ActivatedAt.Should().BeOnOrAfter(before);
    }

    [Fact]
    public void ClearBlocker_adds_history_entry()
    {
        var task = NewTask();
        task.FlagBlocker("waiting", Guid.NewGuid());
        var historyCountBefore = task.History.Count;

        task.ClearBlocker(Guid.NewGuid());

        task.History.Should().HaveCount(historyCountBefore + 1);
        task.History.Last().OldValue.Should().Be(DomainTaskStatus.Blocked.ToString());
    }

    [Fact]
    public void ClearBlocker_on_Active_task_throws_DomainException()
    {
        var task = NewTask();

        var act = () => task.ClearBlocker(Guid.NewGuid());

        act.Should().Throw<DomainException>().WithMessage("*not blocked*");
    }

    // ── ReturnToBacklog ──────────────────────────────────────────────────────────

    [Fact]
    public void ReturnToBacklog_sets_status_to_Backlog_and_clears_assignee_and_due_date()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());

        task.ReturnToBacklog(Guid.NewGuid());

        task.Status.Should().Be(DomainTaskStatus.Backlog);
        task.AssigneeId.Should().BeNull();
        task.DueDate.Should().BeNull();
    }

    [Fact]
    public void ReturnToBacklog_removes_task_from_its_sprint()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        task.AssignToSprint(Guid.NewGuid());

        task.ReturnToBacklog(Guid.NewGuid());

        task.SprintId.Should().BeNull();
    }

    [Fact]
    public void ReturnToBacklog_clears_stale_loan_lineage()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        task.Loan(Guid.NewGuid(), Guid.NewGuid());

        task.ReturnToBacklog(Guid.NewGuid());

        task.LoanedFromEngineerId.Should().BeNull();
    }

    [Fact]
    public void ReturnToBacklog_adds_history_entries_for_status_and_assignee()
    {
        var assigneeId = Guid.NewGuid();
        var task = NewTask();
        task.Assign(assigneeId, Guid.NewGuid());
        var historyCountBefore = task.History.Count;

        task.ReturnToBacklog(Guid.NewGuid());

        task.History.Should().HaveCount(historyCountBefore + 2);
        task.History.Should().Contain(h => h.Field == "status" && h.NewValue == DomainTaskStatus.Backlog.ToString());
        task.History.Should().Contain(h => h.Field == "assignee_id" && h.OldValue == assigneeId.ToString() && h.NewValue == null);
    }

    [Fact]
    public void ReturnToBacklog_on_Backlog_task_throws_DomainException()
    {
        var task = NewTask();

        var act = () => task.ReturnToBacklog(Guid.NewGuid());

        act.Should().Throw<DomainException>().WithMessage("*Only an active task*");
    }

    [Fact]
    public void ReturnToBacklog_on_Blocked_task_throws_DomainException()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        task.FlagBlocker("stuck", Guid.NewGuid());

        var act = () => task.ReturnToBacklog(Guid.NewGuid());

        act.Should().Throw<DomainException>().WithMessage("*Only an active task*");
    }

    // ── Pause ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Pause_sets_status_to_Paused_and_note()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());

        task.Pause("Reprioritized for the week", Guid.NewGuid());

        task.Status.Should().Be(DomainTaskStatus.Paused);
        task.PauseNote.Should().Be("Reprioritized for the week");
    }

    [Fact]
    public void Pause_allows_a_null_note()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());

        task.Pause(null, Guid.NewGuid());

        task.Status.Should().Be(DomainTaskStatus.Paused);
        task.PauseNote.Should().BeNull();
    }

    [Fact]
    public void Pause_adds_history_entry()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        var historyCountBefore = task.History.Count;

        task.Pause("note", Guid.NewGuid());

        task.History.Should().HaveCount(historyCountBefore + 1);
        task.History.Last().NewValue.Should().Be(DomainTaskStatus.Paused.ToString());
    }

    [Fact]
    public void Pause_from_Blocked_transitions_to_Paused_and_remembers_prior_status()
    {
        var task = NewTask();
        task.FlagBlocker("waiting", Guid.NewGuid());

        task.Pause("note", Guid.NewGuid());

        task.Status.Should().Be(DomainTaskStatus.Paused);
        task.StatusBeforePause.Should().Be(DomainTaskStatus.Blocked);
        task.BlockerReason.Should().Be("waiting", "the blocker reason should survive a pause so it's restored on resume");
    }

    [Fact]
    public void Pause_records_PausedAt_and_defaults_PausedByProject_to_false()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        var before = DateTime.UtcNow;

        task.Pause("note", Guid.NewGuid());

        task.PausedAt.Should().NotBeNull().And.Subject.Should().BeOnOrAfter(before);
        task.PausedByProject.Should().BeFalse();
    }

    [Fact]
    public void Pause_with_byProject_true_marks_PausedByProject()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());

        task.Pause(null, Guid.NewGuid(), byProject: true);

        task.PausedByProject.Should().BeTrue();
    }

    [Fact]
    public void Pause_on_Done_task_throws_DomainException()
    {
        var task = NewTask();
        task.MarkDone(Guid.NewGuid());

        var act = () => task.Pause("note", Guid.NewGuid());

        act.Should().Throw<DomainException>();
    }

    // ── Resume ────────────────────────────────────────────────────────────────

    [Fact]
    public void Resume_sets_status_to_Active_and_clears_note()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        task.Pause("note", Guid.NewGuid());

        task.Resume(Guid.NewGuid());

        task.Status.Should().Be(DomainTaskStatus.Active);
        task.PauseNote.Should().BeNull();
    }

    [Fact]
    public void Resume_resets_ActivatedAt()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        task.Pause("note", Guid.NewGuid());
        var before = DateTime.UtcNow;

        task.Resume(Guid.NewGuid());

        task.ActivatedAt.Should().BeOnOrAfter(before);
    }

    [Fact]
    public void Resume_adds_history_entry()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        task.Pause("note", Guid.NewGuid());
        var historyCountBefore = task.History.Count;

        task.Resume(Guid.NewGuid());

        task.History.Should().HaveCount(historyCountBefore + 1);
        task.History.Last().OldValue.Should().Be(DomainTaskStatus.Paused.ToString());
    }

    [Fact]
    public void Resume_on_Active_task_throws_DomainException()
    {
        var task = NewTask();

        var act = () => task.Resume(Guid.NewGuid());

        act.Should().Throw<DomainException>().WithMessage("*not paused*");
    }

    [Fact]
    public void Resume_restores_to_Blocked_when_that_was_the_prior_status()
    {
        var task = NewTask();
        task.FlagBlocker("waiting", Guid.NewGuid());
        task.Pause("note", Guid.NewGuid());

        task.Resume(Guid.NewGuid());

        task.Status.Should().Be(DomainTaskStatus.Blocked);
        task.BlockerReason.Should().Be("waiting");
    }

    [Fact]
    public void Resume_on_project_paused_task_throws_DomainException()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        task.Pause(null, Guid.NewGuid(), byProject: true);

        var act = () => task.Resume(Guid.NewGuid());

        act.Should().Throw<DomainException>().WithMessage("*project is on hold*");
    }

    [Fact]
    public void Resume_clears_pause_bookkeeping()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        task.Pause("note", Guid.NewGuid());

        task.Resume(Guid.NewGuid());

        task.StatusBeforePause.Should().BeNull();
        task.PausedAt.Should().BeNull();
        task.PausedByProject.Should().BeFalse();
    }

    // ── ResumeFromProjectHold ─────────────────────────────────────────────────

    [Fact]
    public void ResumeFromProjectHold_on_task_not_paused_by_project_throws_DomainException()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        task.Pause("note", Guid.NewGuid());

        var act = () => task.ResumeFromProjectHold(Guid.NewGuid());

        act.Should().Throw<DomainException>().WithMessage("*not paused by a project hold*");
    }

    [Fact]
    public void ResumeFromProjectHold_on_task_not_paused_throws_DomainException()
    {
        var task = NewTask();

        var act = () => task.ResumeFromProjectHold(Guid.NewGuid());

        act.Should().Throw<DomainException>().WithMessage("*not paused*");
    }

    [Fact]
    public void ResumeFromProjectHold_shifts_due_date_forward_by_days_paused()
    {
        var task = NewTask(dueDaysFromNow: 10);
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        var originalDueDate = task.DueDate!.Value;
        task.Pause(null, Guid.NewGuid(), byProject: true);
        typeof(PulseTask).GetProperty("PausedAt")!.SetValue(task, DateTime.UtcNow.AddDays(-4));

        task.ResumeFromProjectHold(Guid.NewGuid());

        task.DueDate.Should().Be(originalDueDate.AddDays(4));
    }

    [Fact]
    public void ResumeFromProjectHold_with_no_due_date_does_not_throw()
    {
        var task = PulseTask.Create("No due date", 2, Guid.NewGuid());
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        task.Pause(null, Guid.NewGuid(), byProject: true);

        var act = () => task.ResumeFromProjectHold(Guid.NewGuid());

        act.Should().NotThrow();
        task.DueDate.Should().BeNull();
    }

    [Fact]
    public void ResumeFromProjectHold_restores_Active_and_resets_ActivatedAt()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        task.Pause(null, Guid.NewGuid(), byProject: true);
        var before = DateTime.UtcNow;

        task.ResumeFromProjectHold(Guid.NewGuid());

        task.Status.Should().Be(DomainTaskStatus.Active);
        task.ActivatedAt.Should().BeOnOrAfter(before);
    }

    // ── UpdateDetails ─────────────────────────────────────────────────────────

    [Fact]
    public void UpdateDetails_changes_title_and_adds_history()
    {
        var task = NewTask();
        var actorId = Guid.NewGuid();

        task.UpdateDetails("New title", null, null, 3, task.DueDate, actorId);

        task.Title.Should().Be("New title");
        task.History.Should().Contain(h => h.Field == "title" && h.NewValue == "New title");
    }

    [Fact]
    public void UpdateDetails_does_not_add_history_when_nothing_changes()
    {
        var task = NewTask();

        task.UpdateDetails(task.Title, null, null, task.Points, task.DueDate, Guid.NewGuid());

        task.History.Should().BeEmpty();
    }

    [Fact]
    public void UpdateDetails_on_Done_task_throws_DomainException()
    {
        var task = NewTask();
        task.MarkDone(Guid.NewGuid());

        var act = () => task.UpdateDetails("X", null, null, 1, task.DueDate, Guid.NewGuid());

        act.Should().Throw<DomainException>().WithMessage("*done*");
    }

    [Fact]
    public void UpdateDetails_records_points_and_due_date_changes()
    {
        var task = NewTask(points: 2, dueDaysFromNow: 5);
        var newDue = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10));

        task.UpdateDetails(task.Title, null, null, 8, newDue, Guid.NewGuid());

        task.Points.Should().Be(8);
        task.DueDate.Should().Be(newDue);
        task.History.Should().Contain(h => h.Field == "points");
        task.History.Should().Contain(h => h.Field == "due_date");
    }

    // ── SetType ───────────────────────────────────────────────────────────────

    [Fact]
    public void SetType_changes_type_and_adds_history()
    {
        var task = NewTask();

        task.SetType(TaskType.Bug, Guid.NewGuid());

        task.Type.Should().Be(TaskType.Bug);
        task.History.Should().Contain(h => h.Field == "type");
    }

    [Fact]
    public void SetType_to_same_type_does_not_add_history()
    {
        var task = NewTask();

        task.SetType(TaskType.Feature, Guid.NewGuid());

        task.History.Should().BeEmpty();
    }

    // ── Sprint assignment ─────────────────────────────────────────────────────

    [Fact]
    public void AssignToSprint_sets_SprintId()
    {
        var task = NewTask();
        var sprintId = Guid.NewGuid();

        task.AssignToSprint(sprintId);

        task.SprintId.Should().Be(sprintId);
    }

    [Fact]
    public void AssignToSprint_succeeds_for_an_unpointed_task()
    {
        var task = PulseTask.Create("Untriaged", 0, Guid.NewGuid());
        var sprintId = Guid.NewGuid();

        task.AssignToSprint(sprintId);

        task.SprintId.Should().Be(sprintId);
    }

    [Fact]
    public void RemoveFromSprint_clears_SprintId()
    {
        var task = NewTask();
        task.AssignToSprint(Guid.NewGuid());

        task.RemoveFromSprint();

        task.SprintId.Should().BeNull();
    }

    // ── SendToQa ──────────────────────────────────────────────────────────────

    [Fact]
    public void SendToQa_throws_when_the_task_is_itself_a_QA_task()
    {
        var qaTask = NewTask();
        qaTask.SetParentTaskId(Guid.NewGuid());

        var act = () => qaTask.SendToQa(Guid.NewGuid());

        act.Should().Throw<DomainException>().WithMessage("*cannot itself be sent to QA*");
    }

    [Fact]
    public void SendToQa_succeeds_for_a_regular_task_that_requires_qa()
    {
        var task = NewTask();
        task.SetRequiresQa(true);

        task.SendToQa(Guid.NewGuid());

        task.Status.Should().Be(DomainTaskStatus.InQa);
    }

    [Fact]
    public void SendToQa_succeeds_for_an_unpointed_task()
    {
        var task = PulseTask.Create("Untriaged", 0, Guid.NewGuid());
        task.SetRequiresQa(true);

        task.SendToQa(Guid.NewGuid());

        task.Status.Should().Be(DomainTaskStatus.InQa);
    }

    [Fact]
    public void SendToQa_records_SentToQaAt()
    {
        var task = NewTask();
        task.SetRequiresQa(true);

        task.SendToQa(Guid.NewGuid());

        task.SentToQaAt.Should().Be(DateOnly.FromDateTime(DateTime.UtcNow));
    }

    [Fact]
    public void ConfirmQaRejection_clears_SentToQaAt_so_a_resubmission_is_judged_on_its_own_handoff()
    {
        var task = NewTask();
        task.SetRequiresQa(true);
        task.SendToQa(Guid.NewGuid());
        var reviewerId = Guid.NewGuid();
        task.ProposeQaRejection("Needs rework", reviewerId);

        task.ConfirmQaRejection(reviewerId);

        task.SentToQaAt.Should().BeNull();
    }

    [Fact]
    public void AcceptQa_preserves_SentToQaAt_alongside_the_QA_pass_date()
    {
        var task = NewTask();
        task.SetRequiresQa(true);
        task.SendToQa(Guid.NewGuid());
        var sentToQaAt = task.SentToQaAt;

        task.AcceptQa(Guid.NewGuid());

        // The deadline was already met at handoff — AcceptQa records the (possibly much later)
        // QA-pass date in ActualEndDate, but must not disturb the earlier SentToQaAt the lateness
        // calculation actually freezes against.
        task.SentToQaAt.Should().Be(sentToQaAt);
        task.ActualEndDate.Should().Be(DateOnly.FromDateTime(DateTime.UtcNow));
    }

    [Fact]
    public void MarkDone_leaves_SentToQaAt_null_for_a_task_with_no_QA_step()
    {
        var task = NewTask();

        task.MarkDone(Guid.NewGuid());

        task.SentToQaAt.Should().BeNull();
    }

    // ── SetRequiresFrontendHandoff / HandOffToFrontend ───────────────────────────

    [Fact]
    public void SetRequiresFrontendHandoff_true_starts_the_task_at_Backend_stage()
    {
        var task = NewTask();

        task.SetRequiresFrontendHandoff(true);

        task.RequiresFrontendHandoff.Should().BeTrue();
        task.CurrentStage.Should().Be(TaskStage.Backend);
    }

    [Fact]
    public void SetRequiresFrontendHandoff_false_clears_CurrentStage()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        task.SetRequiresFrontendHandoff(true);
        task.HandOffToFrontend(Guid.NewGuid(), Guid.NewGuid());

        task.SetRequiresFrontendHandoff(false);

        task.RequiresFrontendHandoff.Should().BeFalse();
        task.CurrentStage.Should().BeNull();
    }

    [Fact]
    public void SetRequiresFrontendHandoff_throws_on_a_QA_task()
    {
        var qaTask = NewTask();
        qaTask.SetParentTaskId(Guid.NewGuid());

        var act = () => qaTask.SetRequiresFrontendHandoff(true);

        act.Should().Throw<DomainException>().WithMessage("*QA task*");
    }

    [Fact]
    public void HandOffToFrontend_reassigns_and_advances_the_stage()
    {
        var backendDev = Guid.NewGuid();
        var frontendDev = Guid.NewGuid();
        var task = NewTask();
        task.Assign(backendDev, Guid.NewGuid());
        task.SetRequiresFrontendHandoff(true);

        task.HandOffToFrontend(frontendDev, backendDev);

        task.AssigneeId.Should().Be(frontendDev);
        task.CurrentStage.Should().Be(TaskStage.Frontend);
        // Status is deliberately untouched — a two-stage task never leaves whatever board
        // column it was already in just because the assignee changed.
        task.Status.Should().Be(DomainTaskStatus.Active);
    }

    [Fact]
    public void HandOffToFrontend_records_the_outgoing_backend_developer_as_BackendAssigneeId()
    {
        var backendDev = Guid.NewGuid();
        var frontendDev = Guid.NewGuid();
        var task = NewTask();
        task.Assign(backendDev, Guid.NewGuid());
        task.SetRequiresFrontendHandoff(true);

        task.HandOffToFrontend(frontendDev, backendDev);

        // The backend developer's finished work must stay attributable to them even though
        // AssigneeId has already moved on to Frontend.
        task.BackendAssigneeId.Should().Be(backendDev);
    }

    [Fact]
    public void MarkDone_credits_the_backend_engineer_after_a_frontend_handoff()
    {
        var backendDev = Guid.NewGuid();
        var frontendDev = Guid.NewGuid();
        var task = NewTask();
        task.Assign(backendDev, Guid.NewGuid());
        task.SetRequiresFrontendHandoff(true);
        task.HandOffToFrontend(frontendDev, backendDev);

        task.MarkDone(frontendDev);

        task.History.Last().CreditedEngineerId.Should().Be(backendDev,
            "the backend engineer's share of the work shouldn't disappear just because the frontend engineer finished it");
    }

    [Fact]
    public void MarkDone_prefers_the_loan_over_a_backend_handoff_when_both_apply()
    {
        var backendDev = Guid.NewGuid();
        var frontendDev = Guid.NewGuid();
        var loanedTo = Guid.NewGuid();
        var task = NewTask();
        task.Assign(backendDev, Guid.NewGuid());
        task.SetRequiresFrontendHandoff(true);
        task.HandOffToFrontend(frontendDev, backendDev);
        task.Loan(loanedTo, Guid.NewGuid());

        task.MarkDone(Guid.NewGuid());

        task.History.Last().CreditedEngineerId.Should().Be(frontendDev,
            "the loan is the more recent, more specific signal — it should win over the older backend-handoff attribution");
    }

    [Fact]
    public void HandOffToBackend_keeps_BackendAssigneeId_in_sync_with_the_new_backend_developer()
    {
        var backendDev = Guid.NewGuid();
        var frontendDev = Guid.NewGuid();
        var anotherBackendDev = Guid.NewGuid();
        var task = NewTask();
        task.Assign(backendDev, Guid.NewGuid());
        task.SetRequiresFrontendHandoff(true);
        task.HandOffToFrontend(frontendDev, backendDev);

        task.HandOffToBackend(anotherBackendDev, Guid.NewGuid());

        task.BackendAssigneeId.Should().Be(anotherBackendDev);
    }

    [Fact]
    public void HandOffToFrontend_throws_when_the_task_is_not_flagged()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());

        var act = () => task.HandOffToFrontend(Guid.NewGuid(), Guid.NewGuid());

        act.Should().Throw<DomainException>().WithMessage("*not flagged*");
    }

    [Fact]
    public void HandOffToFrontend_throws_when_already_handed_off()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        task.SetRequiresFrontendHandoff(true);
        task.HandOffToFrontend(Guid.NewGuid(), Guid.NewGuid());

        var act = () => task.HandOffToFrontend(Guid.NewGuid(), Guid.NewGuid());

        act.Should().Throw<DomainException>().WithMessage("*already been handed off*");
    }

    [Fact]
    public void HandOffToFrontend_throws_when_the_task_is_done()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        task.SetRequiresFrontendHandoff(true);
        task.MarkDone(Guid.NewGuid());

        var act = () => task.HandOffToFrontend(Guid.NewGuid(), Guid.NewGuid());

        act.Should().Throw<DomainException>().WithMessage("*done or awaiting QA*");
    }

    // ── HandOffToBackend ──────────────────────────────────────────────────────

    [Fact]
    public void HandOffToBackend_reverses_a_prior_HandOffToFrontend()
    {
        var backendDev = Guid.NewGuid();
        var frontendDev = Guid.NewGuid();
        var anotherBackendDev = Guid.NewGuid();
        var task = NewTask();
        task.Assign(backendDev, Guid.NewGuid());
        task.SetRequiresFrontendHandoff(true);
        task.HandOffToFrontend(frontendDev, backendDev);

        task.HandOffToBackend(anotherBackendDev, frontendDev);

        task.AssigneeId.Should().Be(anotherBackendDev);
        task.CurrentStage.Should().Be(TaskStage.Backend);
        task.Status.Should().Be(DomainTaskStatus.Active);
    }

    [Fact]
    public void HandOffToBackend_throws_when_still_at_Backend_stage()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        task.SetRequiresFrontendHandoff(true);

        var act = () => task.HandOffToBackend(Guid.NewGuid(), Guid.NewGuid());

        act.Should().Throw<DomainException>().WithMessage("*not currently with Frontend*");
    }

    [Fact]
    public void HandOffToBackend_throws_when_the_task_is_not_flagged()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());

        var act = () => task.HandOffToBackend(Guid.NewGuid(), Guid.NewGuid());

        act.Should().Throw<DomainException>().WithMessage("*not flagged*");
    }

    // ── ProposeQaRejection / RespondToQaRejection / ConfirmQaRejection / WithdrawQaRejection / AcceptQa ──

    [Fact]
    public void ProposeQaRejection_throws_when_the_task_is_not_in_QA()
    {
        var task = NewTask();
        task.SetRequiresQa(true);

        var act = () => task.ProposeQaRejection("Missing edge case coverage", Guid.NewGuid());

        act.Should().Throw<DomainException>().WithMessage("*not currently in QA*");
    }

    [Fact]
    public void ProposeQaRejection_throws_when_a_rejection_is_already_pending()
    {
        var task = NewTask();
        task.SetRequiresQa(true);
        task.SendToQa(Guid.NewGuid());
        task.ProposeQaRejection("First concern", Guid.NewGuid());

        var act = () => task.ProposeQaRejection("Second concern", Guid.NewGuid());

        act.Should().Throw<DomainException>().WithMessage("*already pending*");
    }

    [Fact]
    public void ProposeQaRejection_leaves_status_and_the_QA_link_untouched()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        task.SetRequiresQa(true);
        task.SendToQa(Guid.NewGuid());
        var reviewerId = Guid.NewGuid();

        task.ProposeQaRejection("Missing edge case coverage", reviewerId);

        task.Status.Should().Be(DomainTaskStatus.InQa);
        task.PendingRejectionReason.Should().Be("Missing edge case coverage");
        task.PendingRejectionActorId.Should().Be(reviewerId);
        task.ReactivationReason.Should().BeNull();
    }

    [Fact]
    public void ProposeQaRejection_records_the_target_stage_QA_flags_it_with()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        task.SetRequiresQa(true);
        task.SendToQa(Guid.NewGuid());

        task.ProposeQaRejection("Actually a backend bug", Guid.NewGuid(), TaskStage.Backend);

        task.PendingRejectionTargetStage.Should().Be(TaskStage.Backend);
    }

    [Fact]
    public void ConfirmQaRejection_clears_the_pending_target_stage_alongside_the_rest()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        task.SetRequiresQa(true);
        task.SendToQa(Guid.NewGuid());
        var rejecterId = Guid.NewGuid();
        task.ProposeQaRejection("Actually a backend bug", rejecterId, TaskStage.Backend);

        task.ConfirmQaRejection(rejecterId);

        task.PendingRejectionTargetStage.Should().BeNull();
    }

    [Fact]
    public void WithdrawQaRejection_clears_the_pending_target_stage_alongside_the_rest()
    {
        var task = NewTask();
        task.SetRequiresQa(true);
        task.SendToQa(Guid.NewGuid());
        task.ProposeQaRejection("Actually a backend bug", Guid.NewGuid(), TaskStage.Backend);

        task.WithdrawQaRejection(Guid.NewGuid());

        task.PendingRejectionTargetStage.Should().BeNull();
    }

    [Fact]
    public void RespondToQaRejection_throws_when_nothing_is_pending()
    {
        var task = NewTask();
        task.SetRequiresQa(true);
        task.SendToQa(Guid.NewGuid());

        var act = () => task.RespondToQaRejection("It's an environment issue", Guid.NewGuid());

        act.Should().Throw<DomainException>().WithMessage("*no pending QA rejection*");
    }

    [Fact]
    public void RespondToQaRejection_records_the_response_without_resolving_the_rejection()
    {
        var task = NewTask();
        task.SetRequiresQa(true);
        task.SendToQa(Guid.NewGuid());
        task.ProposeQaRejection("Missing edge case coverage", Guid.NewGuid());
        var responderId = Guid.NewGuid();

        task.RespondToQaRejection("It's an environment issue", responderId);

        task.PendingRejectionResponse.Should().Be("It's an environment issue");
        task.PendingRejectionRespondedByEngineerId.Should().Be(responderId);
        task.PendingRejectionReason.Should().Be("Missing edge case coverage");
    }

    [Fact]
    public void ConfirmQaRejection_throws_when_nothing_is_pending()
    {
        var task = NewTask();
        task.SetRequiresQa(true);
        task.SendToQa(Guid.NewGuid());

        var act = () => task.ConfirmQaRejection(Guid.NewGuid());

        act.Should().Throw<DomainException>().WithMessage("*no pending QA rejection*");
    }

    [Fact]
    public void ConfirmQaRejection_records_who_rejected_it_alongside_the_reason()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        task.SetRequiresQa(true);
        task.SendToQa(Guid.NewGuid());
        var rejecterId = Guid.NewGuid();
        task.ProposeQaRejection("Missing edge case coverage", rejecterId);

        task.ConfirmQaRejection(rejecterId);

        task.Status.Should().Be(DomainTaskStatus.Active);
        task.ReactivationReason.Should().Be("Missing edge case coverage");
        task.ReactivatedByEngineerId.Should().Be(rejecterId);
        task.PendingRejectionReason.Should().BeNull();
    }

    [Fact]
    public void WithdrawQaRejection_throws_when_nothing_is_pending()
    {
        var task = NewTask();
        task.SetRequiresQa(true);
        task.SendToQa(Guid.NewGuid());

        var act = () => task.WithdrawQaRejection(Guid.NewGuid());

        act.Should().Throw<DomainException>().WithMessage("*no pending QA rejection*");
    }

    [Fact]
    public void WithdrawQaRejection_clears_the_pending_state_and_leaves_the_task_in_QA()
    {
        var task = NewTask();
        task.SetRequiresQa(true);
        task.SendToQa(Guid.NewGuid());
        task.ProposeQaRejection("Missing edge case coverage", Guid.NewGuid());

        task.WithdrawQaRejection(Guid.NewGuid());

        task.Status.Should().Be(DomainTaskStatus.InQa);
        task.PendingRejectionReason.Should().BeNull();
        task.PendingRejectionActorId.Should().BeNull();
        task.ReactivationReason.Should().BeNull();
    }

    [Fact]
    public void AcceptQa_clears_the_prior_rejecter_once_the_task_is_finally_accepted()
    {
        var task = NewTask();
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        task.SetRequiresQa(true);
        task.SendToQa(Guid.NewGuid());
        var rejecterId = Guid.NewGuid();
        task.ProposeQaRejection("Needs rework", rejecterId);
        task.ConfirmQaRejection(rejecterId);
        task.SendToQa(Guid.NewGuid());

        task.AcceptQa(Guid.NewGuid());

        task.ReactivationReason.Should().BeNull();
        task.ReactivatedByEngineerId.Should().BeNull();
    }

    [Fact]
    public void AcceptQa_credits_the_lending_engineer_when_the_task_is_still_on_loan()
    {
        var task = NewTask();
        var lender = Guid.NewGuid();
        task.Assign(lender, Guid.NewGuid());
        task.Loan(Guid.NewGuid(), Guid.NewGuid());
        task.SetRequiresQa(true);
        task.SendToQa(Guid.NewGuid());

        task.AcceptQa(Guid.NewGuid());

        task.History.Last().CreditedEngineerId.Should().Be(lender);
    }

    // ── SetRequiresQa ─────────────────────────────────────────────────────────

    [Fact]
    public void SetRequiresQa_true_throws_when_the_task_is_itself_a_QA_task()
    {
        var qaTask = NewTask();
        qaTask.SetParentTaskId(Guid.NewGuid());

        var act = () => qaTask.SetRequiresQa(true);

        act.Should().Throw<DomainException>().WithMessage("*cannot itself require QA*");
    }

    [Fact]
    public void SetRequiresQa_false_is_allowed_on_a_QA_task()
    {
        var qaTask = NewTask();
        qaTask.SetParentTaskId(Guid.NewGuid());

        qaTask.SetRequiresQa(false);

        qaTask.RequiresQa.Should().BeFalse();
    }

    // ── PR approval ───────────────────────────────────────────────────────────

    [Fact]
    public void SetRequiresPrApproval_true_throws_when_the_task_is_itself_a_QA_task()
    {
        var qaTask = NewTask();
        qaTask.SetParentTaskId(Guid.NewGuid());

        var act = () => qaTask.SetRequiresPrApproval(true);

        act.Should().Throw<DomainException>().WithMessage("*cannot itself require PR approval*");
    }

    [Fact]
    public void RequestPrApproval_throws_when_not_flagged_as_requiring_it()
    {
        var task = NewTask();

        var act = () => task.RequestPrApproval("https://bitbucket.org/org/repo/pull-requests/1", Guid.NewGuid());

        act.Should().Throw<DomainException>().WithMessage("*not flagged as requiring PR approval*");
    }

    [Fact]
    public void RequestPrApproval_sets_PrLink_and_pending_fields()
    {
        var task = NewTask();
        task.SetRequiresPrApproval(true);
        var requesterId = Guid.NewGuid();

        task.RequestPrApproval("https://bitbucket.org/org/repo/pull-requests/1", requesterId);

        task.PrLink.Should().Be("https://bitbucket.org/org/repo/pull-requests/1");
        task.PendingPrApprovalRequestedAt.Should().NotBeNull();
        task.PendingPrApprovalRequestedByEngineerId.Should().Be(requesterId);
    }

    [Fact]
    public void RequestPrApproval_twice_throws_DomainException()
    {
        var task = NewTask();
        task.SetRequiresPrApproval(true);
        task.RequestPrApproval("https://bitbucket.org/org/repo/pull-requests/1", Guid.NewGuid());

        var act = () => task.RequestPrApproval("https://bitbucket.org/org/repo/pull-requests/2", Guid.NewGuid());

        act.Should().Throw<DomainException>().WithMessage("*already pending*");
    }

    [Fact]
    public void ApprovePrApproval_throws_when_nothing_is_pending()
    {
        var task = NewTask();
        task.SetRequiresPrApproval(true);

        var act = () => task.ApprovePrApproval(Guid.NewGuid());

        act.Should().Throw<DomainException>().WithMessage("*No PR approval request is currently pending*");
    }

    [Fact]
    public void ApprovePrApproval_sets_PrApprovedAt_and_clears_pending_fields()
    {
        var task = NewTask();
        task.SetRequiresPrApproval(true);
        task.RequestPrApproval("https://bitbucket.org/org/repo/pull-requests/1", Guid.NewGuid());

        task.ApprovePrApproval(Guid.NewGuid());

        task.PrApprovedAt.Should().NotBeNull();
        task.PendingPrApprovalRequestedAt.Should().BeNull();
        task.PendingPrApprovalRequestedByEngineerId.Should().BeNull();
    }

    [Fact]
    public void RejectPrApproval_clears_PrLink_and_pending_fields()
    {
        var task = NewTask();
        task.SetRequiresPrApproval(true);
        task.RequestPrApproval("https://bitbucket.org/org/repo/pull-requests/1", Guid.NewGuid());

        task.RejectPrApproval(Guid.NewGuid());

        task.PrLink.Should().BeNull();
        task.PendingPrApprovalRequestedAt.Should().BeNull();
        task.PrApprovedAt.Should().BeNull();
    }

    [Fact]
    public void RejectPrApproval_throws_when_nothing_is_pending()
    {
        var task = NewTask();
        task.SetRequiresPrApproval(true);

        var act = () => task.RejectPrApproval(Guid.NewGuid());

        act.Should().Throw<DomainException>().WithMessage("*No PR approval request is currently pending*");
    }

    [Fact]
    public void ReassignPrApprover_sets_the_delegate_while_pending()
    {
        var task = NewTask();
        task.SetRequiresPrApproval(true);
        task.RequestPrApproval("https://bitbucket.org/org/repo/pull-requests/1", Guid.NewGuid());
        var delegateId = Guid.NewGuid();

        task.ReassignPrApprover(delegateId, Guid.NewGuid());

        task.PendingPrApprovalDelegatedToEngineerId.Should().Be(delegateId);
    }

    [Fact]
    public void ReassignPrApprover_throws_when_nothing_is_pending()
    {
        var task = NewTask();
        task.SetRequiresPrApproval(true);

        var act = () => task.ReassignPrApprover(Guid.NewGuid(), Guid.NewGuid());

        act.Should().Throw<DomainException>().WithMessage("*No PR approval request is currently pending*");
    }

    // ── SetPriority ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void SetPriority_accepts_values_in_range(int priority)
    {
        var task = NewTask();

        task.SetPriority(priority);

        task.Priority.Should().Be(priority);
    }

    [Fact]
    public void SetPriority_accepts_null_to_leave_the_task_unprioritized()
    {
        var task = NewTask();
        task.SetPriority(3);

        task.SetPriority(null);

        task.Priority.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    public void SetPriority_rejects_values_outside_1_to_5(int priority)
    {
        var task = NewTask();

        var act = () => task.SetPriority(priority);

        act.Should().Throw<DomainException>().WithMessage("*between 1 and 5*");
        task.Priority.Should().BeNull("a rejected priority must not have applied");
    }

    // ── AssignTaskNumber / ExternalReference ─────────────────────────────────

    [Fact]
    public void AssignTaskNumber_sets_the_number()
    {
        var task = NewTask();

        task.AssignTaskNumber(11);

        task.TaskNumber.Should().Be(11);
    }

    [Fact]
    public void AssignTaskNumber_twice_throws()
    {
        var task = NewTask();
        task.AssignTaskNumber(11);

        var act = () => task.AssignTaskNumber(12);

        act.Should().Throw<DomainException>().WithMessage("*already been assigned*");
        task.TaskNumber.Should().Be(11, "the rejected second call must not overwrite the first");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AssignTaskNumber_rejects_non_positive_values(int number)
    {
        var task = NewTask();

        var act = () => task.AssignTaskNumber(number);

        act.Should().Throw<DomainException>().WithMessage("*positive*");
    }

    [Fact]
    public void SetExternalReference_sets_and_clears_the_value()
    {
        var task = NewTask();

        task.SetExternalReference("JIRA-482");
        task.ExternalReference.Should().Be("JIRA-482");

        task.SetExternalReference(null);
        task.ExternalReference.Should().BeNull();
    }

    [Fact]
    public void SetPoints_puts_the_change_on_the_record_with_who_and_why()
    {
        var actor = Guid.NewGuid();
        var task = PulseTask.Create("T", 3, Guid.NewGuid(), dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)));

        task.SetPoints(8, actor, reason: "Re-estimated");

        var entry = task.History.Single(h => h.Field == "points");
        entry.OldValue.Should().Be("3");
        entry.NewValue.Should().Be("8");
        entry.ActorId.Should().Be(actor);
        entry.Reason.Should().Be("Re-estimated");
    }

    [Fact]
    public void SetPoints_leaves_no_record_when_nothing_changed()
    {
        var task = PulseTask.Create("T", 3, Guid.NewGuid(), dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)));

        task.SetPoints(3, Guid.NewGuid());

        task.History.Should().NotContain(h => h.Field == "points");
    }

    // ── Returning from QA when the QA task is gone ────────────────────────────

    private static PulseTask InQaWithLink(out Guid qaTaskId)
    {
        var task = NewTask();
        task.SetRequiresQa(true);
        task.SendToQa(Guid.NewGuid());
        qaTaskId = Guid.NewGuid();
        task.SetQaTaskId(qaTaskId);
        return task;
    }

    [Fact]
    public void ReturnFromQaWithoutQaTask_puts_the_task_back_to_Active_and_drops_the_dead_link()
    {
        var task = InQaWithLink(out _);

        task.ReturnFromQaWithoutQaTask(Guid.NewGuid(), "Its QA task was deleted");

        task.Status.Should().Be(DomainTaskStatus.Active);
        task.QaTaskId.Should().BeNull();
        task.SentToQaAt.Should().BeNull();
    }

    [Fact]
    public void ReturnFromQaWithoutQaTask_is_not_a_QA_rejection()
    {
        var task = InQaWithLink(out _);

        task.ReturnFromQaWithoutQaTask(Guid.NewGuid(), "Its QA task was deleted");

        task.ReactivationReason.Should().BeNull("no reviewer rejected anything, so no iteration is counted");
        task.ReactivatedByEngineerId.Should().BeNull();
    }

    [Fact]
    public void ReturnFromQaWithoutQaTask_records_the_status_change_and_the_lost_link_with_the_reason()
    {
        var task = InQaWithLink(out var qaTaskId);

        task.ReturnFromQaWithoutQaTask(Guid.NewGuid(), "Its QA task was deleted");

        task.History.Should().Contain(h => h.Field == "status" && h.NewValue == "Active" && h.Reason == "Its QA task was deleted");
        task.History.Should().Contain(h => h.Field == "qa_task_id" && h.OldValue == qaTaskId.ToString() && h.NewValue == null);
    }

    [Fact]
    public void ReturnFromQaWithoutQaTask_works_when_the_link_itself_was_never_set()
    {
        var task = NewTask();
        task.SetRequiresQa(true);
        task.SendToQa(Guid.NewGuid());   // In QA, no QaTaskId

        task.ReturnFromQaWithoutQaTask(Guid.NewGuid(), "x");

        task.Status.Should().Be(DomainTaskStatus.Active);
        task.History.Should().NotContain(h => h.Field == "qa_task_id");
    }

    [Fact]
    public void ReturnFromQaWithoutQaTask_throws_when_the_task_is_not_in_QA()
    {
        var act = () => NewTask().ReturnFromQaWithoutQaTask(Guid.NewGuid(), "x");

        act.Should().Throw<DomainException>().WithMessage("*not currently in QA*");
    }
}
