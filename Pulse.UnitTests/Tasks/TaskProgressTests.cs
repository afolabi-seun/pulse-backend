using Pulse.Domain.Common;
using Pulse.Domain.Tasks;
using FluentAssertions;
using TaskStatus = Pulse.Domain.Tasks.TaskStatus;

namespace Pulse.UnitTests.Tasks;

public class TaskProgressTests
{
    [Theory]
    [InlineData(TaskStatus.Backlog, 50, 0, 0, 0)]      // not started, whatever was ticked
    [InlineData(TaskStatus.Done, 0, 0, 0, 100)]
    [InlineData(TaskStatus.InQa, 25, 0, 0, 90)]        // only the review is left
    [InlineData(TaskStatus.Active, 0, 0, 0, 0)]
    [InlineData(TaskStatus.Active, 50, 0, 0, 50)]      // no checklist: the step set by hand
    [InlineData(TaskStatus.Blocked, 25, 0, 0, 25)]
    [InlineData(TaskStatus.Active, 50, 1, 4, 25)]      // a checklist overrides the hand-set step
    [InlineData(TaskStatus.Active, 0, 2, 4, 50)]
    [InlineData(TaskStatus.Active, 0, 4, 4, 75)]       // all ticked still stops at 75: 100 is finishing the task
    [InlineData(TaskStatus.Active, 75, 3, 3, 75)]
    [InlineData(TaskStatus.Paused, 50, 0, 0, 50)]      // a pause does not take progress away
    public void Effective_progress_follows_the_status_checklist_and_manual_step(TaskStatus status, int manual, int done, int total, int expected) =>
        TaskProgress.Effective(status, manual, done, total).Should().Be(expected);

    [Fact]
    public void Earned_points_are_points_times_progress_and_remaining_is_the_rest()
    {
        TaskProgress.EarnedAt(8, 50).Should().Be(4m);
        TaskProgress.EarnedAt(3, 25).Should().Be(0.75m);
        TaskProgress.EarnedAt(5, 90).Should().Be(4.5m);
        TaskProgress.EarnedAt(0, 75).Should().Be(0m);
        TaskProgress.Remaining(8, 50).Should().Be(4m);
        TaskProgress.Remaining(13, 100).Should().Be(0m);
    }

    // ── PulseTask.SetProgress ───────────────────────────────────────────────

    private static PulseTask ActiveTask(int points = 8)
    {
        var task = PulseTask.Create("Work", points, Guid.NewGuid());
        task.Assign(Guid.NewGuid(), Guid.NewGuid());
        return task;
    }

    [Theory]
    [InlineData(0)] [InlineData(25)] [InlineData(50)] [InlineData(75)]
    public void The_manual_steps_are_accepted_on_a_task_being_worked_on(int percent)
    {
        var task = ActiveTask();
        task.SetProgress(percent == 0 ? 25 : percent, Guid.NewGuid()); // move off the default first so 0 is a real change too
        if (percent == 0) task.SetProgress(0, Guid.NewGuid());

        task.ProgressPercent.Should().Be(percent);
    }

    [Theory]
    [InlineData(100)] [InlineData(30)] [InlineData(-5)] [InlineData(76)]
    public void Anything_but_the_steps_is_refused_and_100_is_reached_by_finishing(int percent)
    {
        var task = ActiveTask();

        var act = () => task.SetProgress(percent, Guid.NewGuid());

        act.Should().Throw<DomainException>();
        task.ProgressPercent.Should().Be(0);
    }

    [Theory]
    [InlineData(TaskStatus.Backlog)]
    [InlineData(TaskStatus.Paused)]
    [InlineData(TaskStatus.InQa)]
    [InlineData(TaskStatus.Done)]
    public void Progress_can_only_be_set_on_work_in_flight(TaskStatus status)
    {
        var task = ActiveTask();
        switch (status)
        {
            case TaskStatus.Backlog: task.ReturnToBacklog(Guid.NewGuid()); break;
            case TaskStatus.Paused: task.Pause("hold", Guid.NewGuid()); break;
            case TaskStatus.InQa: task.SetRequiresQa(true); task.SendToQa(Guid.NewGuid()); break;
            case TaskStatus.Done: task.MarkDone(Guid.NewGuid()); break;
        }

        var act = () => task.SetProgress(50, Guid.NewGuid());

        act.Should().Throw<DomainException>().WithMessage("*being worked on*");
    }

    [Fact]
    public void A_change_is_recorded_in_the_task_history_and_the_same_value_is_not()
    {
        var task = ActiveTask();
        var actor = Guid.NewGuid();

        task.SetProgress(50, actor);
        task.SetProgress(50, actor);

        task.History.Where(h => h.Field == "progress").Should().ContainSingle()
            .Which.Should().Match<TaskHistory>(h => h.OldValue == "0" && h.NewValue == "50" && h.ActorId == actor);
    }

    [Fact]
    public void Returning_a_task_to_the_backlog_starts_its_progress_again()
    {
        var task = ActiveTask();
        task.SetProgress(75, Guid.NewGuid());

        task.ReturnToBacklog(Guid.NewGuid());

        task.ProgressPercent.Should().Be(0);
    }

    [Fact]
    public void The_engineer_points_are_credited_to_is_the_one_a_finished_task_is_credited_to()
    {
        var task = ActiveTask();
        var original = task.AssigneeId;

        task.CreditedEngineer.Should().Be(original);
    }
}
