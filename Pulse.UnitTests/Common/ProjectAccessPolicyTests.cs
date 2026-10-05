using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Projects;
using Pulse.Domain.Engineers;
using Pulse.Domain.Projects;
using Pulse.Domain.Tasks;
using Pulse.Domain.Teams;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Common;

public class ProjectAccessPolicyTests
{
    private readonly Mock<IProjectRepository> _projects = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IProjectFollowRepository> _follows = new();

    private ProjectAccessPolicy Policy() => new(_projects.Object, _engineers.Object, _teams.Object, _tasks.Object, _follows.Object);

    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _actorId = Guid.NewGuid();

    // ── Individual contributors: members only ─────────────────────────────────

    [Fact]
    public async Task Engineer_who_is_a_member_is_allowed()
    {
        _projects.Setup(r => r.IsMemberAsync(_projectId, _actorId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var allowed = await Policy().CanAccessProjectAsync(_projectId, _actorId, Roles.Engineer);

        allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Engineer_who_is_not_a_member_is_denied()
    {
        _projects.Setup(r => r.IsMemberAsync(_projectId, _actorId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var allowed = await Policy().CanAccessProjectAsync(_projectId, _actorId, Roles.Engineer);

        allowed.Should().BeFalse();
    }

    // ── Assigned-task leg (mirrors ListMyProjectsAsync / GET /projects/mine) ──

    [Fact]
    public async Task Engineer_with_only_an_assigned_task_in_the_project_is_allowed()
    {
        // Not a member, not on the owning team — connected only via an assigned task. GET
        // /projects/mine already lists a project on this basis, so this must too.
        _projects.Setup(r => r.IsMemberAsync(_projectId, _actorId, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _tasks.Setup(r => r.HasAssignedTaskInProjectAsync(_actorId, _projectId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var allowed = await Policy().CanAccessProjectAsync(_projectId, _actorId, Roles.Engineer);

        allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Department_head_with_only_an_assigned_task_in_an_out_of_department_project_is_allowed()
    {
        // The assigned-task leg applies uniformly regardless of role — it short-circuits before
        // the department/team lookups even run.
        _projects.Setup(r => r.IsMemberAsync(_projectId, _actorId, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _tasks.Setup(r => r.HasAssignedTaskInProjectAsync(_actorId, _projectId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var allowed = await Policy().CanAccessProjectAsync(_projectId, _actorId, Roles.HeadOfRnD);

        allowed.Should().BeTrue();
        _teams.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Designer_who_is_not_a_member_is_denied()
    {
        _projects.Setup(r => r.IsMemberAsync(_projectId, _actorId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var allowed = await Policy().CanAccessProjectAsync(_projectId, _actorId, Roles.Designer);

        allowed.Should().BeFalse();
    }

    // ── Managers / PMO: organisation-wide ─────────────────────────────────────

    [Theory]
    [InlineData("project_manager")]
    [InlineData("product_manager")]
    [InlineData("head_of_pmo")]
    [InlineData("head_of_product")]
    public async Task Global_roles_are_allowed_without_membership(string role)
    {
        // No IsMemberAsync / repository setup — global roles must not depend on it.
        var allowed = await Policy().CanAccessProjectAsync(_projectId, _actorId, role);

        allowed.Should().BeTrue();
        _projects.Verify(r => r.IsMemberAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Department heads: scoped to their department ──────────────────────────

    [Fact]
    public async Task Department_head_is_allowed_for_a_project_owned_by_their_department()
    {
        var actorTeamId = Guid.NewGuid();
        var ownerTeamId = Guid.NewGuid();

        var actor = Engineer.Create("Head", "head@test.io", "hash", Roles.HeadOfRnD, 20, 14);
        actor.AssignToTeam(actorTeamId);
        _engineers.Setup(r => r.GetByIdAsync(_actorId, It.IsAny<CancellationToken>())).ReturnsAsync(actor);

        _teams.Setup(r => r.GetByIdAsync(actorTeamId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Team.Create("RnD A", null, "RnD"));
        _teams.Setup(r => r.GetByIdAsync(ownerTeamId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Team.Create("RnD B", null, "RnD"));
        _projects.Setup(r => r.GetByIdAsync(_projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Project.Create("Proj", null, ownerTeamId));

        var allowed = await Policy().CanAccessProjectAsync(_projectId, _actorId, Roles.HeadOfRnD);

        allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Head_of_core_banking_is_allowed_for_a_project_owned_by_their_department()
    {
        var actorTeamId = Guid.NewGuid();
        var ownerTeamId = Guid.NewGuid();

        var actor = Engineer.Create("Head", "head@test.io", "hash", Roles.HeadOfCoreBanking, 20, 14);
        actor.AssignToTeam(actorTeamId);
        _engineers.Setup(r => r.GetByIdAsync(_actorId, It.IsAny<CancellationToken>())).ReturnsAsync(actor);

        _teams.Setup(r => r.GetByIdAsync(actorTeamId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Team.Create("Core Banking A", null, "Core Banking"));
        _teams.Setup(r => r.GetByIdAsync(ownerTeamId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Team.Create("Core Banking B", null, "Core Banking"));
        _projects.Setup(r => r.GetByIdAsync(_projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Project.Create("Proj", null, ownerTeamId));

        var allowed = await Policy().CanAccessProjectAsync(_projectId, _actorId, Roles.HeadOfCoreBanking);

        allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Head_of_core_banking_is_denied_for_a_project_owned_by_another_department()
    {
        var actorTeamId = Guid.NewGuid();
        var ownerTeamId = Guid.NewGuid();

        var actor = Engineer.Create("Head", "head@test.io", "hash", Roles.HeadOfCoreBanking, 20, 14);
        actor.AssignToTeam(actorTeamId);
        _engineers.Setup(r => r.GetByIdAsync(_actorId, It.IsAny<CancellationToken>())).ReturnsAsync(actor);

        _teams.Setup(r => r.GetByIdAsync(actorTeamId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Team.Create("Core Banking A", null, "Core Banking"));
        _teams.Setup(r => r.GetByIdAsync(ownerTeamId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Team.Create("RnD A", null, "RnD"));
        _projects.Setup(r => r.GetByIdAsync(_projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Project.Create("Proj", null, ownerTeamId));
        // Not following the project either, so the fallback check also denies.
        _follows.Setup(f => f.GetAsync(_actorId, _projectId, It.IsAny<CancellationToken>())).ReturnsAsync((ProjectFollow?)null);

        var allowed = await Policy().CanAccessProjectAsync(_projectId, _actorId, Roles.HeadOfCoreBanking);

        allowed.Should().BeFalse();
    }

    [Fact]
    public async Task Head_of_infra_devops_is_allowed_for_a_project_owned_by_their_department()
    {
        var actorTeamId = Guid.NewGuid();
        var ownerTeamId = Guid.NewGuid();

        var actor = Engineer.Create("Head", "head@test.io", "hash", Roles.HeadOfInfraDevOps, 20, 14);
        actor.AssignToTeam(actorTeamId);
        _engineers.Setup(r => r.GetByIdAsync(_actorId, It.IsAny<CancellationToken>())).ReturnsAsync(actor);

        _teams.Setup(r => r.GetByIdAsync(actorTeamId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Team.Create("Infra/DevOps A", null, "Infra/DevOps"));
        _teams.Setup(r => r.GetByIdAsync(ownerTeamId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Team.Create("Infra/DevOps B", null, "Infra/DevOps"));
        _projects.Setup(r => r.GetByIdAsync(_projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Project.Create("Proj", null, ownerTeamId));

        var allowed = await Policy().CanAccessProjectAsync(_projectId, _actorId, Roles.HeadOfInfraDevOps);

        allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Head_of_infra_devops_is_denied_for_a_project_owned_by_another_department()
    {
        var actorTeamId = Guid.NewGuid();
        var ownerTeamId = Guid.NewGuid();

        var actor = Engineer.Create("Head", "head@test.io", "hash", Roles.HeadOfInfraDevOps, 20, 14);
        actor.AssignToTeam(actorTeamId);
        _engineers.Setup(r => r.GetByIdAsync(_actorId, It.IsAny<CancellationToken>())).ReturnsAsync(actor);

        _teams.Setup(r => r.GetByIdAsync(actorTeamId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Team.Create("Infra/DevOps A", null, "Infra/DevOps"));
        _teams.Setup(r => r.GetByIdAsync(ownerTeamId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Team.Create("RnD A", null, "RnD"));
        _projects.Setup(r => r.GetByIdAsync(_projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Project.Create("Proj", null, ownerTeamId));
        // Not following the project either, so the fallback check also denies.
        _follows.Setup(f => f.GetAsync(_actorId, _projectId, It.IsAny<CancellationToken>())).ReturnsAsync((ProjectFollow?)null);

        var allowed = await Policy().CanAccessProjectAsync(_projectId, _actorId, Roles.HeadOfInfraDevOps);

        allowed.Should().BeFalse();
    }

    // ── Team leads: scoped to the team they lead ──────────────────────────────

    [Fact]
    public async Task TeamLead_is_allowed_for_a_project_owned_by_the_team_they_lead()
    {
        var ledTeam = Team.Create("Platform", _actorId);
        _teams.Setup(r => r.ListAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Team> { ledTeam });
        _projects.Setup(r => r.GetByIdAsync(_projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Project.Create("Proj", null, ledTeam.Id));

        var allowed = await Policy().CanAccessProjectAsync(_projectId, _actorId, Roles.TeamLead);

        allowed.Should().BeTrue();
    }

    [Fact]
    public async Task TeamLead_is_denied_for_a_project_owned_by_another_team()
    {
        var ledTeam   = Team.Create("Platform", _actorId);
        var otherTeam = Team.Create("Design", Guid.NewGuid());
        _teams.Setup(r => r.ListAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Team> { ledTeam, otherTeam });
        _projects.Setup(r => r.GetByIdAsync(_projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Project.Create("Proj", null, otherTeam.Id));
        _engineers.Setup(r => r.ListAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Engineer>());
        _projects.Setup(r => r.ListMembersAsync(_projectId, It.IsAny<CancellationToken>())).ReturnsAsync(new List<ProjectMemberDto>());

        var allowed = await Policy().CanAccessProjectAsync(_projectId, _actorId, Roles.TeamLead);

        allowed.Should().BeFalse();
    }

    [Fact]
    public async Task TeamLead_is_allowed_for_a_project_a_member_of_their_team_is_on()
    {
        var ledTeam   = Team.Create("Platform", _actorId);
        var otherTeam = Team.Create("Design", Guid.NewGuid());
        var report = Engineer.Create("Report", "report@test.io", "hash", Roles.Engineer, 20, 14);
        report.AssignToTeam(ledTeam.Id);

        _teams.Setup(r => r.ListAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Team> { ledTeam, otherTeam });
        _projects.Setup(r => r.GetByIdAsync(_projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Project.Create("Proj", null, otherTeam.Id));
        _engineers.Setup(r => r.ListAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Engineer> { report });
        _projects.Setup(r => r.ListMembersAsync(_projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProjectMemberDto> { new(report.Id, "Report", "report@test.io", Roles.Engineer, DateTime.UtcNow) });

        var allowed = await Policy().CanAccessProjectAsync(_projectId, _actorId, Roles.TeamLead);

        allowed.Should().BeTrue();
    }

    [Fact]
    public async Task TeamLead_who_leads_no_team_is_unscoped()
    {
        _teams.Setup(r => r.ListAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Team>());

        var allowed = await Policy().CanAccessProjectAsync(_projectId, _actorId, Roles.TeamLead);

        allowed.Should().BeTrue();
        // Only the personal-project check loads the project; the unscoped lead needs nothing more.
        _projects.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Personal-tasks projects: the owner only ───────────────────────────────

    private Project SeedPersonalProject(Guid ownerId)
    {
        var personal = Project.CreatePersonal(ownerId, "Hannah", "PHR");
        _projects.Setup(r => r.GetByIdAsync(personal.Id, It.IsAny<CancellationToken>())).ReturnsAsync(personal);
        return personal;
    }

    [Fact]
    public async Task Owner_can_access_their_own_personal_project()
    {
        var personal = SeedPersonalProject(_actorId);

        (await Policy().CanAccessProjectAsync(personal.Id, _actorId, Roles.HR)).Should().BeTrue();
    }

    [Theory]
    [InlineData(Roles.HeadOfPmo)]
    [InlineData(Roles.ProjectManager)]
    [InlineData(Roles.ProductManager)]
    [InlineData(Roles.HeadOfProduct)]
    [InlineData(Roles.HeadOfRnD)]
    [InlineData(Roles.TeamLead)]
    [InlineData(Roles.Executive)]
    [InlineData(Roles.Engineer)]
    public async Task Nobody_else_can_access_a_personal_project_whatever_their_role(string role)
    {
        var personal = SeedPersonalProject(Guid.NewGuid());
        // Even a project grant and an assigned task must not open it.
        _projects.Setup(r => r.IsMemberAsync(personal.Id, _actorId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _tasks.Setup(r => r.HasAssignedTaskInProjectAsync(_actorId, personal.Id, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        (await Policy().CanAccessProjectAsync(personal.Id, _actorId, role)).Should().BeFalse();
    }

    [Fact]
    public async Task CanAccessTeam_allows_the_team_a_lead_leads()
    {
        var ledTeam = Team.Create("Platform", _actorId);
        _teams.Setup(r => r.ListAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Team> { ledTeam });

        var allowed = await Policy().CanAccessTeamAsync(ledTeam.Id, _actorId, Roles.TeamLead);

        allowed.Should().BeTrue();
    }

    [Fact]
    public async Task CanAccessTeam_denies_a_lead_a_different_team()
    {
        var ledTeam   = Team.Create("Platform", _actorId);
        var otherTeam = Team.Create("Design", Guid.NewGuid());
        _teams.Setup(r => r.ListAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Team> { ledTeam, otherTeam });

        var allowed = await Policy().CanAccessTeamAsync(otherTeam.Id, _actorId, Roles.TeamLead);

        allowed.Should().BeFalse();
    }

    // ── Team access (sprints) ─────────────────────────────────────────────────

    [Fact]
    public async Task CanAccessTeam_allows_an_engineer_on_that_team()
    {
        var teamId = Guid.NewGuid();
        var actor = Engineer.Create("Dev", "dev@test.io", "hash", Roles.Engineer, 20, 14);
        actor.AssignToTeam(teamId);
        _engineers.Setup(r => r.GetByIdAsync(_actorId, It.IsAny<CancellationToken>())).ReturnsAsync(actor);

        var allowed = await Policy().CanAccessTeamAsync(teamId, _actorId, Roles.Engineer);

        allowed.Should().BeTrue();
    }

    [Fact]
    public async Task CanAccessTeam_denies_an_engineer_on_a_different_team()
    {
        var actor = Engineer.Create("Dev", "dev@test.io", "hash", Roles.Engineer, 20, 14);
        actor.AssignToTeam(Guid.NewGuid());
        _engineers.Setup(r => r.GetByIdAsync(_actorId, It.IsAny<CancellationToken>())).ReturnsAsync(actor);

        var allowed = await Policy().CanAccessTeamAsync(Guid.NewGuid(), _actorId, Roles.Engineer);

        allowed.Should().BeFalse();
    }

    [Fact]
    public async Task CanAccessTeam_allows_global_roles_without_team_membership()
    {
        var allowed = await Policy().CanAccessTeamAsync(Guid.NewGuid(), _actorId, Roles.ProjectManager);

        allowed.Should().BeTrue();
        _engineers.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Sprint access (team OR project-via-tasks) ─────────────────────────────

    [Fact]
    public async Task CanAccessSprint_allows_an_engineer_who_is_a_member_of_a_project_with_a_task_in_the_sprint()
    {
        var sprintId = Guid.NewGuid();
        var otherTeamId = Guid.NewGuid();
        var memberProjectId = Guid.NewGuid();

        // Engineer is NOT on the sprint's team…
        var actor = Engineer.Create("Dev", "dev@test.io", "hash", Roles.Engineer, 20, 14);
        actor.AssignToTeam(Guid.NewGuid());
        _engineers.Setup(r => r.GetByIdAsync(_actorId, It.IsAny<CancellationToken>())).ReturnsAsync(actor);

        // …but the sprint contains a task in a project they're a member of.
        _tasks.Setup(r => r.IsAssignedInSprintAsync(sprintId, _actorId, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _tasks.Setup(r => r.GetProjectIdsBySprintAsync(sprintId, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { memberProjectId });
        _projects.Setup(r => r.IsMemberAsync(memberProjectId, _actorId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var allowed = await Policy().CanAccessSprintAsync(sprintId, otherTeamId, _actorId, Roles.Engineer);

        allowed.Should().BeTrue();
    }

    [Fact]
    public async Task CanAccessSprint_denies_an_engineer_with_no_team_or_project_link()
    {
        var sprintId = Guid.NewGuid();

        var actor = Engineer.Create("Dev", "dev@test.io", "hash", Roles.Engineer, 20, 14);
        actor.AssignToTeam(Guid.NewGuid());
        _engineers.Setup(r => r.GetByIdAsync(_actorId, It.IsAny<CancellationToken>())).ReturnsAsync(actor);

        _tasks.Setup(r => r.IsAssignedInSprintAsync(sprintId, _actorId, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _tasks.Setup(r => r.GetProjectIdsBySprintAsync(sprintId, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { Guid.NewGuid() });
        _projects.Setup(r => r.IsMemberAsync(It.IsAny<Guid>(), _actorId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var allowed = await Policy().CanAccessSprintAsync(sprintId, Guid.NewGuid(), _actorId, Roles.Engineer);

        allowed.Should().BeFalse();
    }

    [Fact]
    public async Task Department_head_is_denied_for_a_project_owned_by_another_department()
    {
        var actorTeamId = Guid.NewGuid();
        var ownerTeamId = Guid.NewGuid();

        var actor = Engineer.Create("Head", "head@test.io", "hash", Roles.HeadOfRnD, 20, 14);
        actor.AssignToTeam(actorTeamId);
        _engineers.Setup(r => r.GetByIdAsync(_actorId, It.IsAny<CancellationToken>())).ReturnsAsync(actor);

        _teams.Setup(r => r.GetByIdAsync(actorTeamId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Team.Create("RnD A", null, "RnD"));
        _teams.Setup(r => r.GetByIdAsync(ownerTeamId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Team.Create("Design A", null, "Design"));
        _projects.Setup(r => r.GetByIdAsync(_projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Project.Create("Proj", null, ownerTeamId));
        // Not following the project either, so the fallback check also denies.
        _follows.Setup(f => f.GetAsync(_actorId, _projectId, It.IsAny<CancellationToken>())).ReturnsAsync((ProjectFollow?)null);

        var allowed = await Policy().CanAccessProjectAsync(_projectId, _actorId, Roles.HeadOfRnD);

        allowed.Should().BeFalse();
    }

    [Fact]
    public async Task Head_of_functional_is_allowed_for_a_project_owned_by_their_department()
    {
        var actorTeamId = Guid.NewGuid();
        var ownerTeamId = Guid.NewGuid();

        var actor = Engineer.Create("Head", "head@test.io", "hash", Roles.HeadOfFunctional, 20, 14);
        actor.AssignToTeam(actorTeamId);
        _engineers.Setup(r => r.GetByIdAsync(_actorId, It.IsAny<CancellationToken>())).ReturnsAsync(actor);

        _teams.Setup(r => r.GetByIdAsync(actorTeamId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Team.Create("Functional A", null, "Functional"));
        _teams.Setup(r => r.GetByIdAsync(ownerTeamId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Team.Create("Functional B", null, "Functional"));
        _projects.Setup(r => r.GetByIdAsync(_projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Project.Create("Proj", null, ownerTeamId));

        var allowed = await Policy().CanAccessProjectAsync(_projectId, _actorId, Roles.HeadOfFunctional);

        allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Head_of_functional_is_denied_for_a_project_owned_by_another_department()
    {
        var actorTeamId = Guid.NewGuid();
        var ownerTeamId = Guid.NewGuid();

        var actor = Engineer.Create("Head", "head@test.io", "hash", Roles.HeadOfFunctional, 20, 14);
        actor.AssignToTeam(actorTeamId);
        _engineers.Setup(r => r.GetByIdAsync(_actorId, It.IsAny<CancellationToken>())).ReturnsAsync(actor);

        _teams.Setup(r => r.GetByIdAsync(actorTeamId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Team.Create("Functional A", null, "Functional"));
        _teams.Setup(r => r.GetByIdAsync(ownerTeamId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Team.Create("RnD A", null, "RnD"));
        _projects.Setup(r => r.GetByIdAsync(_projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Project.Create("Proj", null, ownerTeamId));
        // Not following the project either, so the fallback check also denies.
        _follows.Setup(f => f.GetAsync(_actorId, _projectId, It.IsAny<CancellationToken>())).ReturnsAsync((ProjectFollow?)null);

        var allowed = await Policy().CanAccessProjectAsync(_projectId, _actorId, Roles.HeadOfFunctional);

        allowed.Should().BeFalse();
    }

    [Fact]
    public async Task Department_head_is_allowed_for_an_out_of_department_project_they_follow()
    {
        var actorTeam = Team.Create("RnD A", null, "RnD");
        var ownerTeam = Team.Create("Product A", null, "Product");

        var actor = Engineer.Create("Head", "head@test.io", "hash", Roles.HeadOfRnD, 20, 14);
        actor.AssignToTeam(actorTeam.Id);
        _engineers.Setup(r => r.GetByIdAsync(_actorId, It.IsAny<CancellationToken>())).ReturnsAsync(actor);

        _teams.Setup(r => r.GetByIdAsync(actorTeam.Id, It.IsAny<CancellationToken>())).ReturnsAsync(actorTeam);
        _teams.Setup(r => r.GetByIdAsync(ownerTeam.Id, It.IsAny<CancellationToken>())).ReturnsAsync(ownerTeam);
        _projects.Setup(r => r.GetByIdAsync(_projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Project.Create("Proj", null, ownerTeam.Id));
        _follows.Setup(f => f.GetAsync(_actorId, _projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProjectFollow.Create(_actorId, _projectId));

        var allowed = await Policy().CanAccessProjectAsync(_projectId, _actorId, Roles.HeadOfRnD);

        allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Department_head_is_denied_for_an_out_of_department_project_their_engineer_is_a_member_of_but_they_dont_follow()
    {
        // This is the case explicitly walked back: an engineer's incidental membership on an
        // unrelated project used to auto-grant the head access. It no longer does — only the
        // head's own department ownership or a deliberate follow does.
        var actorTeam = Team.Create("RnD A", null, "RnD");
        var ownerTeam = Team.Create("Product A", null, "Product");

        var actor = Engineer.Create("Head", "head@test.io", "hash", Roles.HeadOfRnD, 20, 14);
        actor.AssignToTeam(actorTeam.Id);
        var report = Engineer.Create("Report", "report@test.io", "hash", Roles.Engineer, 20, 14);
        report.AssignToTeam(actorTeam.Id);
        _engineers.Setup(r => r.GetByIdAsync(_actorId, It.IsAny<CancellationToken>())).ReturnsAsync(actor);

        _teams.Setup(r => r.GetByIdAsync(actorTeam.Id, It.IsAny<CancellationToken>())).ReturnsAsync(actorTeam);
        _teams.Setup(r => r.GetByIdAsync(ownerTeam.Id, It.IsAny<CancellationToken>())).ReturnsAsync(ownerTeam);
        _projects.Setup(r => r.GetByIdAsync(_projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Project.Create("Proj", null, ownerTeam.Id));
        _projects.Setup(r => r.ListMembersAsync(_projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProjectMemberDto> { new(report.Id, "Report", "report@test.io", Roles.Engineer, DateTime.UtcNow) });
        _follows.Setup(f => f.GetAsync(_actorId, _projectId, It.IsAny<CancellationToken>())).ReturnsAsync((ProjectFollow?)null);

        var allowed = await Policy().CanAccessProjectAsync(_projectId, _actorId, Roles.HeadOfRnD);

        allowed.Should().BeFalse();
    }

    [Fact]
    public async Task Department_head_with_explicit_membership_is_allowed_outside_their_department()
    {
        // Explicit project membership must win even when the project is owned by a different
        // department — being added to a project is a deliberate grant that department-scoping
        // must not silently override.
        var actorTeamId = Guid.NewGuid();
        var ownerTeamId = Guid.NewGuid();

        var actor = Engineer.Create("Head", "head@test.io", "hash", Roles.HeadOfRnD, 20, 14);
        actor.AssignToTeam(actorTeamId);
        _engineers.Setup(r => r.GetByIdAsync(_actorId, It.IsAny<CancellationToken>())).ReturnsAsync(actor);
        _projects.Setup(r => r.IsMemberAsync(_projectId, _actorId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var allowed = await Policy().CanAccessProjectAsync(_projectId, _actorId, Roles.HeadOfRnD);

        allowed.Should().BeTrue();
        _teams.Verify(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── GetAccessibleEngineerIdsAsync — the @mention candidate set ─────────────

    private void SeedEmptyProject()
    {
        _projects.Setup(r => r.GetByIdAsync(_projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Project.Create("Proj", null, null));
        _projects.Setup(r => r.ListMembersAsync(_projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProjectMemberDto>());
        _tasks.Setup(r => r.GetByProjectAsync(_projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PulseTask>());
    }

    [Fact]
    public async Task Accessible_engineers_always_includes_global_role_engineers()
    {
        var pm = Engineer.Create("PM", "pm@test.io", "hash", Roles.ProjectManager, 20, 14);
        var ic = Engineer.Create("IC", "ic@test.io", "hash", Roles.Engineer, 20, 14);
        _engineers.Setup(r => r.ListActiveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { pm, ic });
        SeedEmptyProject();

        var ids = await Policy().GetAccessibleEngineerIdsAsync(_projectId);

        ids.Should().Contain(pm.Id).And.NotContain(ic.Id);
    }

    [Fact]
    public async Task Accessible_engineers_includes_explicit_members()
    {
        var member = Engineer.Create("Member", "member@test.io", "hash", Roles.Engineer, 20, 14);
        _engineers.Setup(r => r.ListActiveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<Engineer>());
        SeedEmptyProject();
        _projects.Setup(r => r.ListMembersAsync(_projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProjectMemberDto> { new(member.Id, member.Name, member.Email, member.Role, DateTime.UtcNow) });

        var ids = await Policy().GetAccessibleEngineerIdsAsync(_projectId);

        ids.Should().Contain(member.Id);
    }

    [Fact]
    public async Task Accessible_engineers_includes_task_assignees()
    {
        var assignee = Engineer.Create("Assignee", "assignee@test.io", "hash", Roles.Engineer, 20, 14);
        var task = PulseTask.Create("T", 3, _projectId);
        task.Assign(assignee.Id, Guid.NewGuid());
        _engineers.Setup(r => r.ListActiveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<Engineer>());
        SeedEmptyProject();
        _tasks.Setup(r => r.GetByProjectAsync(_projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PulseTask> { task });

        var ids = await Policy().GetAccessibleEngineerIdsAsync(_projectId);

        ids.Should().Contain(assignee.Id);
    }

    [Fact]
    public async Task Accessible_engineers_includes_the_whole_owning_team_roster_not_just_its_members()
    {
        // "member of my team" should be mentionable even if nobody explicitly added them as a
        // project member — being on the team that owns the project is its own standing grant.
        var ownerTeam = Team.Create("Owners");
        var teammate = Engineer.Create("Teammate", "teammate@test.io", "hash", Roles.Engineer, 20, 14);
        teammate.AssignToTeam(ownerTeam.Id);
        _engineers.Setup(r => r.ListActiveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { teammate });
        SeedEmptyProject();
        _projects.Setup(r => r.GetByIdAsync(_projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Project.Create("Proj", null, ownerTeam.Id));
        _teams.Setup(r => r.GetByIdAsync(ownerTeam.Id, It.IsAny<CancellationToken>())).ReturnsAsync(ownerTeam);

        var ids = await Policy().GetAccessibleEngineerIdsAsync(_projectId);

        ids.Should().Contain(teammate.Id);
    }

    [Fact]
    public async Task Accessible_engineers_includes_the_owning_teams_lead_even_though_the_lead_is_not_its_own_roster_member()
    {
        // Team.TeamLeadId, not the lead's own Engineer.TeamId, is the source of truth for which
        // team someone leads (see ProjectAccessPolicy's other department/team-lead tests) — a lead
        // whose own TeamId points elsewhere would otherwise be silently dropped from the roster scan.
        var leadId = Guid.NewGuid();
        var ownerTeam = Team.Create("Owners", teamLeadId: leadId);
        _engineers.Setup(r => r.ListActiveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<Engineer>());
        SeedEmptyProject();
        _projects.Setup(r => r.GetByIdAsync(_projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Project.Create("Proj", null, ownerTeam.Id));
        _teams.Setup(r => r.GetByIdAsync(ownerTeam.Id, It.IsAny<CancellationToken>())).ReturnsAsync(ownerTeam);

        var ids = await Policy().GetAccessibleEngineerIdsAsync(_projectId);

        ids.Should().Contain(leadId);
    }

    [Fact]
    public async Task Accessible_engineers_includes_a_department_head_whose_department_owns_the_project()
    {
        var ownerTeam = Team.Create("Owners", department: "R&D");
        var headTeam = Team.Create("Leadership", department: "R&D");
        var head = Engineer.Create("Head", "head@test.io", "hash", Roles.HeadOfRnD, 20, 14);
        head.AssignToTeam(headTeam.Id);
        _engineers.Setup(r => r.ListActiveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { head });
        SeedEmptyProject();
        _projects.Setup(r => r.GetByIdAsync(_projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Project.Create("Proj", null, ownerTeam.Id));
        _teams.Setup(r => r.GetByIdAsync(ownerTeam.Id, It.IsAny<CancellationToken>())).ReturnsAsync(ownerTeam);
        _teams.Setup(r => r.GetByIdAsync(headTeam.Id, It.IsAny<CancellationToken>())).ReturnsAsync(headTeam);

        var ids = await Policy().GetAccessibleEngineerIdsAsync(_projectId);

        ids.Should().Contain(head.Id);
    }

    [Fact]
    public async Task Accessible_engineers_excludes_a_department_head_from_a_different_department()
    {
        var ownerTeam = Team.Create("Owners", department: "R&D");
        var headTeam = Team.Create("Leadership", department: "Design");
        var head = Engineer.Create("Head", "head@test.io", "hash", Roles.HeadOfDesign, 20, 14);
        head.AssignToTeam(headTeam.Id);
        _engineers.Setup(r => r.ListActiveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { head });
        SeedEmptyProject();
        _projects.Setup(r => r.GetByIdAsync(_projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Project.Create("Proj", null, ownerTeam.Id));
        _teams.Setup(r => r.GetByIdAsync(ownerTeam.Id, It.IsAny<CancellationToken>())).ReturnsAsync(ownerTeam);
        _teams.Setup(r => r.GetByIdAsync(headTeam.Id, It.IsAny<CancellationToken>())).ReturnsAsync(headTeam);

        var ids = await Policy().GetAccessibleEngineerIdsAsync(_projectId);

        ids.Should().NotContain(head.Id);
    }

    [Fact]
    public async Task Accessible_engineers_excludes_someone_with_no_connection_to_the_project()
    {
        var stranger = Engineer.Create("Stranger", "stranger@test.io", "hash", Roles.Engineer, 20, 14);
        _engineers.Setup(r => r.ListActiveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { stranger });
        SeedEmptyProject();

        var ids = await Policy().GetAccessibleEngineerIdsAsync(_projectId);

        ids.Should().NotContain(stranger.Id);
    }

    // ── CanViewTaskAsync — read-only teammate carve-out ────────────────────────
    // ListTasksHandler already surfaces a teammate's/department-mate's task regardless of
    // project access; CanViewTaskAsync exists so the detail page doesn't then 403 it. It must
    // never grant more than CanAccessTaskAsync already does for anyone else, and the write-side
    // commands (Update/Assign/AddComment/StartTimer/SubmitVote) deliberately keep using the
    // stricter CanAccessTaskAsync — that's exercised at the handler level, not here.

    [Fact]
    public async Task CanViewTask_delegates_to_CanAccessTaskAsync_for_the_assignee()
    {
        var task = PulseTask.Create("T", 3, _projectId);
        task.Assign(_actorId, Guid.NewGuid());
        _tasks.Setup(r => r.GetByIdAsync(task.Id, It.IsAny<CancellationToken>())).ReturnsAsync(task);

        var allowed = await Policy().CanViewTaskAsync(task.Id, _actorId, Roles.Engineer);

        allowed.Should().BeTrue();
    }

    [Fact]
    public async Task CanViewTask_returns_false_for_a_missing_task()
    {
        _tasks.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((PulseTask?)null);

        var allowed = await Policy().CanViewTaskAsync(Guid.NewGuid(), _actorId, Roles.TeamLead);

        allowed.Should().BeFalse();
    }

    [Fact]
    public async Task CanViewTask_gives_a_plain_engineer_no_teammate_carve_out()
    {
        var otherTeam = Team.Create("Design", Guid.NewGuid());
        var teammate = Engineer.Create("Teammate", "teammate@test.io", "hash", Roles.Engineer, 20, 14);
        teammate.AssignToTeam(Guid.NewGuid());
        var task = PulseTask.Create("T", 3, _projectId);
        task.Assign(teammate.Id, Guid.NewGuid());

        _tasks.Setup(r => r.GetByIdAsync(task.Id, It.IsAny<CancellationToken>())).ReturnsAsync(task);
        _projects.Setup(r => r.IsMemberAsync(_projectId, _actorId, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _tasks.Setup(r => r.HasAssignedTaskInProjectAsync(_actorId, _projectId, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _engineers.Setup(r => r.GetByIdAsync(_actorId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Engineer.Create("Me", "me@test.io", "hash", Roles.Engineer, 20, 14));
        _projects.Setup(r => r.GetByIdAsync(_projectId, It.IsAny<CancellationToken>())).ReturnsAsync(Project.Create("Proj", null, otherTeam.Id));

        var allowed = await Policy().CanViewTaskAsync(task.Id, _actorId, Roles.Engineer);

        allowed.Should().BeFalse();
    }

    [Fact]
    public async Task CanViewTask_allows_a_TeamLead_to_view_a_teammates_task_in_an_unrelated_project()
    {
        var ledTeam = Team.Create("Platform", _actorId);
        var otherTeam = Team.Create("Design", Guid.NewGuid());
        var teammate = Engineer.Create("Teammate", "teammate@test.io", "hash", Roles.Engineer, 20, 14);
        teammate.AssignToTeam(ledTeam.Id);

        var task = PulseTask.Create("T", 3, _projectId);
        task.Assign(teammate.Id, Guid.NewGuid());

        _tasks.Setup(r => r.GetByIdAsync(task.Id, It.IsAny<CancellationToken>())).ReturnsAsync(task);
        _teams.Setup(r => r.ListAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Team> { ledTeam, otherTeam });
        _projects.Setup(r => r.GetByIdAsync(_projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Project.Create("Proj", null, otherTeam.Id));
        _engineers.Setup(r => r.ListAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Engineer> { teammate });
        _projects.Setup(r => r.ListMembersAsync(_projectId, It.IsAny<CancellationToken>())).ReturnsAsync(new List<ProjectMemberDto>());
        _engineers.Setup(r => r.GetByIdAsync(teammate.Id, It.IsAny<CancellationToken>())).ReturnsAsync(teammate);

        var allowed = await Policy().CanViewTaskAsync(task.Id, _actorId, Roles.TeamLead);

        allowed.Should().BeTrue();
    }

    [Fact]
    public async Task CanViewTask_denies_a_TeamLead_for_a_task_assigned_outside_their_team()
    {
        var ledTeam = Team.Create("Platform", _actorId);
        var otherTeam = Team.Create("Design", Guid.NewGuid());
        var stranger = Engineer.Create("Stranger", "stranger@test.io", "hash", Roles.Engineer, 20, 14);
        stranger.AssignToTeam(otherTeam.Id);

        var task = PulseTask.Create("T", 3, _projectId);
        task.Assign(stranger.Id, Guid.NewGuid());

        _tasks.Setup(r => r.GetByIdAsync(task.Id, It.IsAny<CancellationToken>())).ReturnsAsync(task);
        _teams.Setup(r => r.ListAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Team> { ledTeam, otherTeam });
        _projects.Setup(r => r.GetByIdAsync(_projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Project.Create("Proj", null, otherTeam.Id));
        _engineers.Setup(r => r.ListAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Engineer> { stranger });
        _projects.Setup(r => r.ListMembersAsync(_projectId, It.IsAny<CancellationToken>())).ReturnsAsync(new List<ProjectMemberDto>());
        _engineers.Setup(r => r.GetByIdAsync(stranger.Id, It.IsAny<CancellationToken>())).ReturnsAsync(stranger);

        var allowed = await Policy().CanViewTaskAsync(task.Id, _actorId, Roles.TeamLead);

        allowed.Should().BeFalse();
    }

    [Fact]
    public async Task CanViewTask_allows_a_department_head_to_view_a_department_mates_task_in_an_unrelated_project()
    {
        var headTeam = Team.Create("Leadership", department: "RnD");
        var mateTeam = Team.Create("RnD B", department: "RnD");
        var otherTeam = Team.Create("Design A", department: "Design");

        var head = Engineer.Create("Head", "head@test.io", "hash", Roles.HeadOfRnD, 20, 14);
        head.AssignToTeam(headTeam.Id);
        var mate = Engineer.Create("Mate", "mate@test.io", "hash", Roles.Engineer, 20, 14);
        mate.AssignToTeam(mateTeam.Id);

        var task = PulseTask.Create("T", 3, _projectId);
        task.Assign(mate.Id, Guid.NewGuid());

        _tasks.Setup(r => r.GetByIdAsync(task.Id, It.IsAny<CancellationToken>())).ReturnsAsync(task);
        _engineers.Setup(r => r.GetByIdAsync(_actorId, It.IsAny<CancellationToken>())).ReturnsAsync(head);
        _engineers.Setup(r => r.GetByIdAsync(mate.Id, It.IsAny<CancellationToken>())).ReturnsAsync(mate);
        _teams.Setup(r => r.GetByIdAsync(headTeam.Id, It.IsAny<CancellationToken>())).ReturnsAsync(headTeam);
        _teams.Setup(r => r.GetByIdAsync(mateTeam.Id, It.IsAny<CancellationToken>())).ReturnsAsync(mateTeam);
        _teams.Setup(r => r.GetByIdAsync(otherTeam.Id, It.IsAny<CancellationToken>())).ReturnsAsync(otherTeam);
        _projects.Setup(r => r.GetByIdAsync(_projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Project.Create("Proj", null, otherTeam.Id));
        _follows.Setup(f => f.GetAsync(_actorId, _projectId, It.IsAny<CancellationToken>())).ReturnsAsync((ProjectFollow?)null);

        var allowed = await Policy().CanViewTaskAsync(task.Id, _actorId, Roles.HeadOfRnD);

        allowed.Should().BeTrue();
    }

    [Fact]
    public async Task CanViewTask_denies_a_department_head_for_a_task_assigned_outside_their_department()
    {
        var headTeam = Team.Create("Leadership", department: "RnD");
        var otherDeptTeam = Team.Create("Design B", department: "Design");
        var otherTeam = Team.Create("Design A", department: "Design");

        var head = Engineer.Create("Head", "head@test.io", "hash", Roles.HeadOfRnD, 20, 14);
        head.AssignToTeam(headTeam.Id);
        var stranger = Engineer.Create("Stranger", "stranger@test.io", "hash", Roles.Engineer, 20, 14);
        stranger.AssignToTeam(otherDeptTeam.Id);

        var task = PulseTask.Create("T", 3, _projectId);
        task.Assign(stranger.Id, Guid.NewGuid());

        _tasks.Setup(r => r.GetByIdAsync(task.Id, It.IsAny<CancellationToken>())).ReturnsAsync(task);
        _engineers.Setup(r => r.GetByIdAsync(_actorId, It.IsAny<CancellationToken>())).ReturnsAsync(head);
        _engineers.Setup(r => r.GetByIdAsync(stranger.Id, It.IsAny<CancellationToken>())).ReturnsAsync(stranger);
        _teams.Setup(r => r.GetByIdAsync(headTeam.Id, It.IsAny<CancellationToken>())).ReturnsAsync(headTeam);
        _teams.Setup(r => r.GetByIdAsync(otherDeptTeam.Id, It.IsAny<CancellationToken>())).ReturnsAsync(otherDeptTeam);
        _teams.Setup(r => r.GetByIdAsync(otherTeam.Id, It.IsAny<CancellationToken>())).ReturnsAsync(otherTeam);
        _projects.Setup(r => r.GetByIdAsync(_projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Project.Create("Proj", null, otherTeam.Id));
        _follows.Setup(f => f.GetAsync(_actorId, _projectId, It.IsAny<CancellationToken>())).ReturnsAsync((ProjectFollow?)null);

        var allowed = await Policy().CanViewTaskAsync(task.Id, _actorId, Roles.HeadOfRnD);

        allowed.Should().BeFalse();
    }

    [Fact]
    public async Task CanViewTask_denies_a_TeamLead_for_an_unassigned_task_outside_their_access()
    {
        var ledTeam = Team.Create("Platform", _actorId);
        var otherTeam = Team.Create("Design", Guid.NewGuid());
        var task = PulseTask.Create("T", 3, _projectId); // unassigned

        _tasks.Setup(r => r.GetByIdAsync(task.Id, It.IsAny<CancellationToken>())).ReturnsAsync(task);
        _teams.Setup(r => r.ListAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Team> { ledTeam, otherTeam });
        _projects.Setup(r => r.GetByIdAsync(_projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Project.Create("Proj", null, otherTeam.Id));
        _engineers.Setup(r => r.ListAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Engineer>());
        _projects.Setup(r => r.ListMembersAsync(_projectId, It.IsAny<CancellationToken>())).ReturnsAsync(new List<ProjectMemberDto>());

        var allowed = await Policy().CanViewTaskAsync(task.Id, _actorId, Roles.TeamLead);

        allowed.Should().BeFalse();
    }
}
