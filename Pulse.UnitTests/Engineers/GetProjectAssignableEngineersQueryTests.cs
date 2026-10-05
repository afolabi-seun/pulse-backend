using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Engineers.Queries;
using Pulse.Application.Projects;
using Pulse.Domain.Engineers;
using Pulse.Domain.Projects;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Engineers;

public class GetProjectAssignableEngineersQueryTests
{
    private readonly Mock<IProjectRepository> _projects = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<IProjectAccessPolicy> _access = new();

    public GetProjectAssignableEngineersQueryTests()
    {
        _access
            .Setup(a => a.CanAccessProjectAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
    }

    private GetProjectAssignableEngineersHandler CreateHandler() =>
        new(_projects.Object, _engineers.Object, _access.Object);

    [Fact]
    public async Task Returns_NOT_FOUND_when_project_missing()
    {
        _projects.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((Project?)null);

        var result = await CreateHandler().Handle(new GetProjectAssignableEngineersQuery(Guid.NewGuid(), Guid.NewGuid(), Roles.ProjectManager), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task Returns_FORBIDDEN_when_actor_cannot_access_project()
    {
        var project = Project.Create("Finsys CBS");
        _projects.Setup(r => r.GetByIdAsync(project.Id, default)).ReturnsAsync(project);
        _access
            .Setup(a => a.CanAccessProjectAsync(project.Id, It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await CreateHandler().Handle(new GetProjectAssignableEngineersQuery(project.Id, Guid.NewGuid(), Roles.HeadOfFunctional), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
    }

    [Fact]
    public async Task Returns_owner_teams_roster_even_when_caller_is_in_a_different_department()
    {
        var ownerTeamId = Guid.NewGuid();
        var project = Project.Create("Finsys CBS", ownerTeamId: ownerTeamId);
        var coreBankingEngineer = Engineer.Create("Priya Kapoor", "priya@test.io", "hash", Roles.Engineer, 20, 14);
        coreBankingEngineer.AssignToTeam(ownerTeamId);
        var unrelatedEngineer = Engineer.Create("Someone Else", "else@test.io", "hash", Roles.Engineer, 20, 14);

        _projects.Setup(r => r.GetByIdAsync(project.Id, default)).ReturnsAsync(project);
        _projects.Setup(r => r.ListMembersAsync(project.Id, default)).ReturnsAsync(Array.Empty<ProjectMemberDto>());
        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync(new[] { coreBankingEngineer, unrelatedEngineer });

        var result = await CreateHandler().Handle(new GetProjectAssignableEngineersQuery(project.Id, Guid.NewGuid(), Roles.HeadOfFunctional), default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Should().ContainSingle(e => e.Id == coreBankingEngineer.Id);
        result.Data.Should().NotContain(e => e.Id == unrelatedEngineer.Id);
    }

    [Fact]
    public async Task Includes_an_explicit_project_member_from_outside_the_owner_team()
    {
        var ownerTeamId = Guid.NewGuid();
        var project = Project.Create("Finsys CBS", ownerTeamId: ownerTeamId);
        var crossTeamMember = Engineer.Create("Grace Liu", "grace@test.io", "hash", Roles.Engineer, 20, 14);

        _projects.Setup(r => r.GetByIdAsync(project.Id, default)).ReturnsAsync(project);
        _projects.Setup(r => r.ListMembersAsync(project.Id, default))
            .ReturnsAsync(new[] { new ProjectMemberDto(crossTeamMember.Id, crossTeamMember.Name, crossTeamMember.Email, crossTeamMember.Role, DateTime.UtcNow) });
        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync(new[] { crossTeamMember });

        var result = await CreateHandler().Handle(new GetProjectAssignableEngineersQuery(project.Id, Guid.NewGuid(), Roles.HeadOfFunctional), default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Should().ContainSingle(e => e.Id == crossTeamMember.Id);
    }

    [Fact]
    public async Task Executive_bypasses_CanAccessProjectAsync_entirely()
    {
        var project = Project.Create("Finsys CBS");
        _access
            .Setup(a => a.CanAccessProjectAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _projects.Setup(r => r.GetByIdAsync(project.Id, default)).ReturnsAsync(project);
        _projects.Setup(r => r.ListMembersAsync(project.Id, default)).ReturnsAsync(Array.Empty<ProjectMemberDto>());
        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync(Array.Empty<Engineer>());

        var result = await CreateHandler().Handle(new GetProjectAssignableEngineersQuery(project.Id, Guid.NewGuid(), Roles.Executive), default);

        result.IsSuccess.Should().BeTrue();
        _access.Verify(a => a.CanAccessProjectAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
