using FluentAssertions;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Alerts;
using Pulse.Domain.Engineers;
using Pulse.Infrastructure.GoogleChat;
using Moq;

namespace Pulse.UnitTests.Alerts;

public class HandleGoogleChatReplyJobTests
{
    private readonly Mock<IGoogleChatThreadRepository> _threads = new();
    private readonly Mock<IAlertRuleRepository> _rules = new();
    private readonly Mock<IAlertExplainer> _explainer = new();
    private readonly Mock<IGoogleChatMessenger> _chat = new();
    private readonly Mock<IGoogleChatSpaceRepository> _spaces = new();
    private readonly BackgroundOrganizationContext _organization = new();
    private readonly Guid _spaceOrganization = Guid.NewGuid();

    public HandleGoogleChatReplyJobTests()
    {
        // "spaces/A" is linked to an organization; tests about unlinked spaces use another space.
        var space = GoogleChatSpace.Create("spaces/A", "Alerts");
        space.LinkTo(_spaceOrganization);
        _spaces.Setup(s => s.GetBySpaceIdAsync("spaces/A", default)).ReturnsAsync(space);
    }

    private HandleGoogleChatReplyJob CreateJob() =>
        new(_threads.Object, _rules.Object, _explainer.Object, _chat.Object, _spaces.Object, _organization);

    [Fact]
    public async Task Does_nothing_when_the_thread_is_not_one_Pulse_started()
    {
        _threads.Setup(t => t.FindAsync("spaces/A", "spaces/A/threads/1", default)).ReturnsAsync((GoogleChatThread?)null);

        await CreateJob().ExecuteAsync("spaces/A", "spaces/A/threads/1", "why though?");

        _rules.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), default), Times.Never);
        _chat.Verify(c => c.PostToSpaceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), default), Times.Never);
    }

    [Fact]
    public async Task Does_nothing_when_the_rule_no_longer_exists()
    {
        var thread = GoogleChatThread.Create(Guid.NewGuid(), "spaces/A", "spaces/A/threads/1");
        _threads.Setup(t => t.FindAsync("spaces/A", "spaces/A/threads/1", default)).ReturnsAsync(thread);
        _rules.Setup(r => r.GetByIdAsync(thread.AlertRuleId, default)).ReturnsAsync((AlertRule?)null);

        await CreateJob().ExecuteAsync("spaces/A", "spaces/A/threads/1", "why though?");

        _explainer.Verify(e => e.AnswerFollowUpAsync(It.IsAny<AlertRule>(), It.IsAny<string>(), default), Times.Never);
    }

    [Fact]
    public async Task Stays_silent_when_the_explainer_cannot_produce_an_answer()
    {
        var owner = Engineer.Create("Owner", "owner_gchat@test.io", "hash", Roles.TeamLead, 20, 14);
        var rule = AlertRule.Create(owner.Id, "Watch it", AlertMetric.BlockerCount, AlertScopeType.Team, Guid.NewGuid(),
            AlertComparator.GreaterThan, 5, true, false);
        var thread = GoogleChatThread.Create(rule.Id, "spaces/A", "spaces/A/threads/1");
        _threads.Setup(t => t.FindAsync("spaces/A", "spaces/A/threads/1", default)).ReturnsAsync(thread);
        _rules.Setup(r => r.GetByIdAsync(rule.Id, default)).ReturnsAsync(rule);
        _explainer.Setup(e => e.AnswerFollowUpAsync(rule, "why though?", default)).ReturnsAsync((string?)null);

        await CreateJob().ExecuteAsync("spaces/A", "spaces/A/threads/1", "why though?");

        _chat.Verify(c => c.PostToSpaceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), default), Times.Never);
    }

    [Fact]
    public async Task Posts_the_answer_back_into_the_same_thread()
    {
        var owner = Engineer.Create("Owner", "owner_gchat2@test.io", "hash", Roles.TeamLead, 20, 14);
        var rule = AlertRule.Create(owner.Id, "Watch it", AlertMetric.BlockerCount, AlertScopeType.Team, Guid.NewGuid(),
            AlertComparator.GreaterThan, 5, true, false);
        var thread = GoogleChatThread.Create(rule.Id, "spaces/A", "spaces/A/threads/1");
        _threads.Setup(t => t.FindAsync("spaces/A", "spaces/A/threads/1", default)).ReturnsAsync(thread);
        _rules.Setup(r => r.GetByIdAsync(rule.Id, default)).ReturnsAsync(rule);
        _explainer.Setup(e => e.AnswerFollowUpAsync(rule, "why though?", default)).ReturnsAsync("Because three tasks are blocked.");

        await CreateJob().ExecuteAsync("spaces/A", "spaces/A/threads/1", "why though?");

        _chat.Verify(c => c.PostToSpaceAsync("spaces/A", "Because three tasks are blocked.", "spaces/A/threads/1", default), Times.Once);
    }
    // ── Which organization a reply is handled for (multi-tenancy Phase 2c) ───────

    [Fact]
    public async Task A_reply_in_a_linked_space_is_handled_for_the_spaces_organization()
    {
        _threads.Setup(t => t.FindAsync("spaces/A", "spaces/A/threads/1", default))
            .Callback(() => _organization.OrganizationId.Should().Be(_spaceOrganization))
            .ReturnsAsync((GoogleChatThread?)null);

        await CreateJob().ExecuteAsync("spaces/A", "spaces/A/threads/1", "why?");

        _organization.OrganizationId.Should().Be(_spaceOrganization);
    }

    [Fact]
    public async Task A_reply_in_an_unlinked_space_is_ignored()
    {
        _spaces.Setup(s => s.GetBySpaceIdAsync("spaces/B", default)).ReturnsAsync(GoogleChatSpace.Create("spaces/B", "Unlinked"));

        await CreateJob().ExecuteAsync("spaces/B", "spaces/B/threads/1", "why?");

        _threads.Verify(t => t.FindAsync(It.IsAny<string>(), It.IsAny<string>(), default), Times.Never);
        _organization.OrganizationId.Should().BeNull();
    }
}
