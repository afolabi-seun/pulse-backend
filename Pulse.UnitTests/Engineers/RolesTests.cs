using Pulse.Domain.Engineers;
using FluentAssertions;

namespace Pulse.UnitTests.Engineers;

public class RolesTests
{
    [Theory]
    [InlineData(Roles.Executive)]
    [InlineData(Roles.HR)]
    [InlineData(Roles.Accountant)]
    public void IsOrgReadOnlyViewer_is_true_for_the_org_wide_read_only_roles(string role) =>
        Roles.IsOrgReadOnlyViewer(role).Should().BeTrue();

    [Theory]
    [InlineData(Roles.Engineer)]
    [InlineData(Roles.TeamLead)]
    [InlineData(Roles.ProjectManager)]
    [InlineData(Roles.HeadOfPmo)]
    [InlineData(Roles.HeadOfRnD)]
    [InlineData("")]
    [InlineData(null)]
    public void IsOrgReadOnlyViewer_is_false_for_every_other_role(string? role) =>
        Roles.IsOrgReadOnlyViewer(role).Should().BeFalse();
}
