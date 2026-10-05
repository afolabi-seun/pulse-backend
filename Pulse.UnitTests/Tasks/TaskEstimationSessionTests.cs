using Pulse.Domain.Common;
using Pulse.Domain.Tasks;
using FluentAssertions;

namespace Pulse.UnitTests.Tasks;

public class TaskEstimationSessionTests
{
    private static TaskEstimationSession RevealedSession(Guid? taskId = null)
    {
        var session = TaskEstimationSession.Create(taskId ?? Guid.NewGuid());
        session.Reveal();
        return session;
    }

    [Fact]
    public void EscalateToHead_sets_the_flag()
    {
        var session = RevealedSession();
        session.SubmitForApproval(5, Guid.NewGuid());

        session.EscalateToHead();

        session.EscalatedToHead.Should().BeTrue();
    }

    [Fact]
    public void EscalateToHead_throws_when_nothing_is_pending()
    {
        var session = RevealedSession();

        var act = () => session.EscalateToHead();

        act.Should().Throw<DomainException>().WithMessage("*No estimate is pending approval*");
    }

    [Fact]
    public void SubmitForApproval_resets_a_prior_escalation_on_a_fresh_request()
    {
        var session = RevealedSession();
        session.SubmitForApproval(5, Guid.NewGuid());
        session.EscalateToHead();

        session.ClearApprovalRequest();
        session.SubmitForApproval(8, Guid.NewGuid());

        session.EscalatedToHead.Should().BeFalse();
    }

    [Fact]
    public void ClearApprovalRequest_resets_the_escalation_flag()
    {
        var session = RevealedSession();
        session.SubmitForApproval(5, Guid.NewGuid());
        session.EscalateToHead();

        session.ClearApprovalRequest();

        session.EscalatedToHead.Should().BeFalse();
    }
}
