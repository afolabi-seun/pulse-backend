using Pulse.Domain.Common;
using Pulse.Domain.Projects;
using Pulse.Domain.Tasks;
using FluentAssertions;

namespace Pulse.UnitTests.Projects;

public class PersonalProjectTests
{
    [Fact]
    public void CreatePersonal_marks_the_project_with_its_owner_and_names_it_after_them()
    {
        var owner = Guid.NewGuid();

        var project = Project.CreatePersonal(owner, "Hannah Reyes", "PHR");

        project.PersonalOwnerId.Should().Be(owner);
        project.Name.Should().Be("Hannah Reyes — Personal");
        project.Code.Should().Be("PHR");
    }

    [Fact]
    public void An_ordinary_project_has_no_personal_owner()
    {
        Project.Create("Notifications").PersonalOwnerId.Should().BeNull();
    }

    [Fact]
    public void CreatePersonal_still_enforces_the_project_code_format()
    {
        var act = () => Project.CreatePersonal(Guid.NewGuid(), "Hannah Reyes", "1bad");

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void ActivateAsPersonal_moves_an_unpointed_backlog_task_straight_to_active_and_records_it()
    {
        var actor = Guid.NewGuid();
        var task = PulseTask.Create("Review leave policy", 0, Guid.NewGuid());
        task.Assign(actor, actor);
        task.Status.Should().Be(Pulse.Domain.Tasks.TaskStatus.Backlog);

        task.ActivateAsPersonal(actor);

        task.Status.Should().Be(Pulse.Domain.Tasks.TaskStatus.Active);
        task.History.Should().Contain(h => h.Field == "status" && h.NewValue == "Active");
        task.Points.Should().Be(0, "a personal task is never estimated");
    }

    [Fact]
    public void ActivateAsPersonal_leaves_a_task_that_is_not_in_backlog_alone()
    {
        var actor = Guid.NewGuid();
        var task = PulseTask.Create("Already moving", 3, Guid.NewGuid());
        task.Assign(actor, actor); // pointed + assigned -> already Active
        var historyBefore = task.History.Count;

        task.ActivateAsPersonal(actor);

        task.Status.Should().Be(Pulse.Domain.Tasks.TaskStatus.Active);
        task.History.Should().HaveCount(historyBefore);
    }
}
