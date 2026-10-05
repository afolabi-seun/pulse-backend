using Pulse.Application.CheckIns;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.CheckIns;

public class CheckInVisibilityTests
{
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITeamRepository> _teams = new();

    private Task<bool> CanView(Guid actor, string role, Guid target) =>
        CheckInVisibility.CanViewAsync(actor, role, target, _engineers.Object, _teams.Object, default);

    [Theory]
    [InlineData(Roles.Executive)]
    [InlineData(Roles.HR)]
    [InlineData(Roles.Accountant)]
    public async Task Org_read_only_roles_can_view_any_engineers_check_ins(string role)
    {
        // No team, no repository setup: they must be allowed on role alone, not by sharing a team.
        (await CanView(Guid.NewGuid(), role, Guid.NewGuid())).Should().BeTrue();
    }

    [Fact]
    public async Task Engineer_still_cannot_view_someone_elses_check_ins()
    {
        (await CanView(Guid.NewGuid(), Roles.Engineer, Guid.NewGuid())).Should().BeFalse();
    }

    [Fact]
    public async Task Team_lead_without_a_team_still_cannot_view_others()
    {
        var target = Engineer.Create("Target", "t@ex.com", "hash", Roles.Engineer, 10, 5);
        var lead = Engineer.Create("Lead", "l@ex.com", "hash", Roles.TeamLead, 10, 5);
        _engineers.Setup(r => r.GetByIdAsync(target.Id, default)).ReturnsAsync(target);
        _engineers.Setup(r => r.GetByIdAsync(lead.Id, default)).ReturnsAsync(lead);

        (await CanView(lead.Id, Roles.TeamLead, target.Id)).Should().BeFalse();
    }
}
