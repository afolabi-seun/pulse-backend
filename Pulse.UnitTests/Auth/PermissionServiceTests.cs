using Pulse.Application.Auth;
using Pulse.Domain.Engineers;
using FluentAssertions;

namespace Pulse.UnitTests.Auth;

public class PermissionServiceTests
{
    // ── head_of_rd gets every permission including thresholds ─────────────────

    [Fact]
    public void Head_of_rd_has_thresholds_manage()
    {
        var perms = PermissionService.For(Roles.HeadOfRnD);
        perms.Should().Contain("thresholds:manage");
    }

    [Fact]
    public void Head_of_rd_has_all_head_shared_permissions()
    {
        var perms = PermissionService.For(Roles.HeadOfRnD);
        perms.Should().Contain("feedback:view")
             .And.Contain("users:manage")
             .And.Contain("audit:view");
    }

    // ── new head roles get shared head permissions but NOT thresholds ─────────

    [Theory]
    [InlineData(Roles.HeadOfProduct)]
    [InlineData(Roles.HeadOfDesign)]
    [InlineData(Roles.HeadOfPmo)]
    [InlineData(Roles.HeadOfFunctional)]
    [InlineData(Roles.HeadOfCoreBanking)]
    [InlineData(Roles.HeadOfInfraDevOps)]
    public void Non_rd_head_does_not_have_thresholds_manage(string role)
    {
        var perms = PermissionService.For(role);
        perms.Should().NotContain("thresholds:manage",
            because: $"{role} cannot manage thresholds — that is head_of_rd only");
    }

    [Theory]
    [InlineData(Roles.HeadOfProduct)]
    [InlineData(Roles.HeadOfDesign)]
    [InlineData(Roles.HeadOfPmo)]
    [InlineData(Roles.HeadOfFunctional)]
    [InlineData(Roles.HeadOfCoreBanking)]
    [InlineData(Roles.HeadOfInfraDevOps)]
    public void Non_rd_head_has_shared_head_permissions(string role)
    {
        var perms = PermissionService.For(role);
        perms.Should().Contain("feedback:view")
             .And.Contain("users:manage")
             .And.Contain("audit:view");
    }

    // ── role hierarchy — each level inherits the level below ─────────────────

    [Fact]
    public void All_roles_have_dashboard_view()
    {
        foreach (var role in Roles.All)
        {
            PermissionService.For(role).Should().Contain("dashboard:view",
                because: $"{role} must be able to view the dashboard");
        }
    }

    [Fact]
    public void Project_manager_does_not_have_feedback_view()
    {
        PermissionService.For(Roles.ProjectManager)
            .Should().NotContain("feedback:view");
    }

    [Fact]
    public void Engineer_does_not_have_users_manage()
    {
        PermissionService.For(Roles.Engineer)
            .Should().NotContain("users:manage");
    }
}
