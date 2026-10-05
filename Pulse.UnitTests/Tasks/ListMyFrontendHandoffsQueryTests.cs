using Pulse.Application.Common.Interfaces;
using Pulse.Application.Tasks.Queries;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Tasks;

public class ListMyFrontendHandoffsQueryTests
{
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<IProjectRepository> _projects = new();

    private ListMyFrontendHandoffsHandler CreateHandler() => new(_tasks.Object, _engineers.Object, _projects.Object);

    [Fact]
    public async Task Resolves_project_and_current_assignee_names_for_a_handed_off_task()
    {
        var backendDev = Guid.NewGuid();
        var frontendDevEngineer = Engineer.Create("Frontend Dev", "fe@test.io", "hash", Roles.Engineer, 20, 14);
        var frontendDev = frontendDevEngineer.Id;
        var projectId = Guid.NewGuid();

        var task = PulseTask.Create("Wire up the API", 5, projectId,
            dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)));
        task.Assign(backendDev, Guid.NewGuid());
        task.SetRequiresFrontendHandoff(true);
        task.HandOffToFrontend(frontendDev, backendDev);

        _tasks.Setup(r => r.GetHandedOffByAsync(backendDev, default)).ReturnsAsync(new[] { task });
        _projects.Setup(p => p.GetNamesByIdsAsync(It.Is<IReadOnlyList<Guid>>(ids => ids.Contains(projectId)), default))
            .ReturnsAsync(new Dictionary<Guid, string> { [projectId] = "Loans Platform" });
        _projects.Setup(p => p.GetCodesByIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), default))
            .ReturnsAsync(new Dictionary<Guid, string>());
        _engineers.Setup(e => e.GetByIdsAsync(It.Is<IReadOnlyList<Guid>>(ids => ids.Contains(frontendDev)), default))
            .ReturnsAsync(new[] { frontendDevEngineer });

        var result = await CreateHandler().Handle(new ListMyFrontendHandoffsQuery(backendDev), default);

        result.IsSuccess.Should().BeTrue();
        var dto = result.Data.Should().ContainSingle().Which;
        dto.Id.Should().Be(task.Id);
        dto.ProjectName.Should().Be("Loans Platform");
        dto.AssigneeName.Should().Be("Frontend Dev");
        dto.BackendAssigneeId.Should().Be(backendDev);
    }

    [Fact]
    public async Task Returns_empty_when_the_caller_has_nothing_handed_off()
    {
        var actorId = Guid.NewGuid();
        _tasks.Setup(r => r.GetHandedOffByAsync(actorId, default)).ReturnsAsync(Array.Empty<PulseTask>());

        var result = await CreateHandler().Handle(new ListMyFrontendHandoffsQuery(actorId), default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Should().BeEmpty();
    }
}
