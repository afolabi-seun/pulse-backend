using Pulse.Application.Overwork;
using Pulse.Application.Reports;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using FluentAssertions;
using TaskStatus = Pulse.Domain.Tasks.TaskStatus;

namespace Pulse.UnitTests.Reports;

public class UtilizationEntryBuilderTests
{
    private readonly Engineer _ada = Engineer.Create("Ada", "ada@x.io", "hash", Roles.Engineer, 20, 14);

    private PulseTask Task(int points, TaskStatus status = TaskStatus.Active, decimal earned = 0m, bool assigned = true)
    {
        var t = PulseTask.Create("t", points, Guid.NewGuid(), dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)));
        if (assigned) t.Assign(_ada.Id, Guid.NewGuid());
        if (status == TaskStatus.Paused) t.Pause("hold", Guid.NewGuid());
        t.SetEarnedPoints(earned);
        return t;
    }

    private EngineerUtilizationEntry Build(IReadOnlyList<PulseTask> active, EngineerWorkload workload,
        IReadOnlyDictionary<Guid, (int Paused, int Unestimated)>? notCounted = null) =>
        UtilizationEntryBuilder.Build(
            _ada,
            new Dictionary<Guid, IReadOnlyList<PulseTask>> { [_ada.Id] = active },
            new Dictionary<Guid, IReadOnlyList<PulseTask>>(),
            new Dictionary<Guid, int>(), new Dictionary<Guid, int>(), new Dictionary<Guid, decimal>(),
            new Dictionary<Guid, int>(), new Dictionary<Guid, int>(),
            new Dictionary<Guid, EngineerWorkload> { [_ada.Id] = workload },
            notCounted);

    [Fact]
    public void Points_is_the_workload_figure_so_it_agrees_with_due_this_cycle_whichever_way_points_are_counted()
    {
        var tasks = new List<PulseTask> { Task(8, earned: 6m), Task(5) };

        // With progress counted the assessor says 7 points (2 + 5) are still to do; with it off, all 13.
        var counted = Build(tasks, new EngineerWorkload(2, 7, 7, false));
        var notCounted = Build(tasks, new EngineerWorkload(2, 13, 13, false));

        counted.TotalPoints.Should().Be(7);
        notCounted.TotalPoints.Should().Be(13);
        counted.TotalPoints.Should().Be(counted.CyclePoints, "everything here is due this cycle, so the two must agree");
    }

    [Fact]
    public void The_engineer_has_paused_and_unestimated_counts_from_the_lookup()
    {
        var entry = Build([Task(3)], new EngineerWorkload(1, 3, 3, false),
            new Dictionary<Guid, (int, int)> { [_ada.Id] = (2, 4) });

        entry.PausedTasks.Should().Be(2);
        entry.UnestimatedBacklogTasks.Should().Be(4);
    }

    [Fact]
    public void An_engineer_missing_from_the_lookup_has_none()
    {
        var entry = Build([Task(3)], new EngineerWorkload(1, 3, 3, false), new Dictionary<Guid, (int, int)>());

        entry.PausedTasks.Should().Be(0);
        entry.UnestimatedBacklogTasks.Should().Be(0);
    }

    [Fact]
    public void NotCountedByAssignee_counts_paused_tasks_and_assigned_unestimated_backlog_tasks_only()
    {
        var other = Guid.NewGuid();
        var unassignedBacklog = Task(0, assigned: false);
        var tasks = new List<PulseTask>
        {
            Task(5, TaskStatus.Paused),                 // paused
            Task(3, TaskStatus.Paused),                 // paused
            Task(0, TaskStatus.Backlog),                // assigned to Ada but never estimated, so it stays in the Backlog
            Task(5),                                    // active: counted in the workload instead
            unassignedBacklog,                          // nobody's
        };

        var result = UtilizationEntryBuilder.NotCountedByAssignee(tasks);

        result[_ada.Id].Should().Be((2, 1));
        result.Should().NotContainKey(other);
        result.Should().HaveCount(1);
    }
}
