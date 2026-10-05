using Pulse.Domain.Common;
using Pulse.Domain.Tasks;
using FluentAssertions;

namespace Pulse.UnitTests.Tasks;

public class SubtaskTests
{
    private static Subtask NewSubtask() => Subtask.Create(Guid.NewGuid(), "Checklist item", Guid.NewGuid());

    [Fact]
    public void Loan_sets_the_assignee_and_remembers_no_prior_holder_when_never_loaned_before()
    {
        var subtask = NewSubtask();
        var engineerId = Guid.NewGuid();

        subtask.Loan(engineerId);

        subtask.AssigneeId.Should().Be(engineerId);
        subtask.LoanedFromEngineerId.Should().BeNull();
    }

    [Fact]
    public void Recall_restores_null_when_the_subtask_had_never_been_loaned_before()
    {
        var subtask = NewSubtask();
        var engineerId = Guid.NewGuid();
        subtask.Loan(engineerId);

        subtask.Recall();

        subtask.AssigneeId.Should().BeNull();
        subtask.LoanedFromEngineerId.Should().BeNull();
    }

    [Fact]
    public void Re_loaning_an_already_loaned_subtask_is_allowed_and_updates_the_lineage()
    {
        var subtask = NewSubtask();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        subtask.Loan(first);
        subtask.Loan(second);

        subtask.AssigneeId.Should().Be(second);
        subtask.LoanedFromEngineerId.Should().Be(first, "lineage should track only the immediately previous holder");
    }

    [Fact]
    public void Recall_after_a_re_loan_restores_only_the_immediately_previous_holder()
    {
        // Mirrors PulseTask.Recall's own one-level-back semantics — recalling never reaches
        // further back than the loan being undone.
        var subtask = NewSubtask();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        subtask.Loan(first);
        subtask.Loan(second);

        subtask.Recall();

        subtask.AssigneeId.Should().Be(first);
        subtask.LoanedFromEngineerId.Should().BeNull();
    }

    [Fact]
    public void Recall_throws_when_the_subtask_is_not_currently_loaned()
    {
        var subtask = NewSubtask();

        var act = () => subtask.Recall();

        act.Should().Throw<DomainException>().WithMessage("*not currently loaned*");
    }
}
