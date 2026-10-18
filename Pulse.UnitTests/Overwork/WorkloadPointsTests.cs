using Pulse.Application.Overwork;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using FluentAssertions;

namespace Pulse.UnitTests.Overwork;

public class WorkloadPointsTests
{
    private static PulseTask Task(int points, decimal earned, DateOnly? due = null)
    {
        var t = PulseTask.Create("t", points, Guid.NewGuid(), dueDate: due ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)));
        t.Assign(Guid.NewGuid(), Guid.NewGuid());
        t.SetEarnedPoints(earned);
        return t;
    }

    [Fact]
    public void Without_progress_a_task_weighs_all_its_points()
    {
        WorkloadPoints.Of(Task(8, 6m), useProgress: false).Should().Be(8m);
        WorkloadPoints.Sum([Task(8, 6m), Task(5, 0m)], useProgress: false).Should().Be(13);
    }

    [Fact]
    public void With_progress_a_task_weighs_only_what_is_still_to_do()
    {
        WorkloadPoints.Of(Task(8, 6m), useProgress: true).Should().Be(2m);
        WorkloadPoints.Sum([Task(8, 6m), Task(5, 0m)], useProgress: true).Should().Be(7);
    }

    [Fact]
    public void The_sum_is_rounded_to_whole_points_and_never_below_zero()
    {
        WorkloadPoints.Sum([Task(3, 0.75m)], useProgress: true).Should().Be(2, "2.25 rounds to 2");
        WorkloadPoints.Sum([Task(5, 2.5m)], useProgress: true).Should().Be(3, "2.5 rounds away from zero");
        WorkloadPoints.Of(Task(2, 5m), useProgress: true).Should().Be(0m);
    }

    [Fact]
    public void The_overwork_signal_sees_the_lighter_load_only_when_progress_is_counted()
    {
        var engineer = Engineer.Create("Ada", "ada@x.io", "hash", Roles.Engineer, baselinePoints: 10, baselineCycleDays: 14);
        var tasks = new List<PulseTask> { Task(13, 10m), Task(8, 6m) }; // 21 points on paper, 5 still to do

        var off = new OverworkSignalsCalculator(new OverworkThresholds { UseProgressInWorkload = false }).Compute(engineer, tasks, null);
        var on = new OverworkSignalsCalculator(new OverworkThresholds { UseProgressInWorkload = true }).Compute(engineer, tasks, null);

        off.Signals.ActivePoints.Should().Be(21);
        off.Signals.LoadVsBaseline.Tripped.Should().BeTrue("21 points due this cycle is over 1.3 times a baseline of 10");
        on.Signals.ActivePoints.Should().Be(5);
        on.Signals.LoadVsBaseline.Tripped.Should().BeFalse("only 5 points are still to do");
    }

    [Fact]
    public void A_departments_own_thresholds_still_use_the_organizations_setting()
    {
        var global = new OverworkThresholds { UseProgressInWorkload = true };
        var resolved = DepartmentThresholdResolver.Resolve(global, new DepartmentThresholdOverride());

        resolved.UseProgressInWorkload.Should().BeTrue();
    }
}
