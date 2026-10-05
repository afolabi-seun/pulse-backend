using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Tasks;
using Pulse.Domain.Engineers;
using Pulse.Domain.Projects;
using Pulse.Domain.Tasks;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Tasks;

public class TaskCreationPolicyTests
{
    private readonly Mock<IProjectRepository> _projects = new();
    private readonly Mock<IProjectAccessPolicy> _access = new();
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _actorId = Guid.NewGuid();

    private TaskCreationInput Input(
        int points = 0, DateOnly? dueDate = null, Guid? assigneeId = null, int? priority = null) =>
        new("Title", null, null, points, dueDate, _projectId, assigneeId, TaskType.Feature, null,
            false, null, null, priority, _actorId, Roles.Engineer, TaskNumber: 1);

    [Fact]
    public async Task Fails_with_not_found_when_the_project_is_archived()
    {
        var project = Project.Create("Proj");
        project.Archive();
        _projects.Setup(p => p.GetByIdAsync(_projectId, default)).ReturnsAsync(project);

        var result = await TaskCreationPolicy.ValidateAndBuildAsync(Input(), _projects.Object, _access.Object, default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task Fails_with_business_rule_violation_when_the_project_is_paused()
    {
        var project = Project.Create("Proj");
        project.Pause();
        _projects.Setup(p => p.GetByIdAsync(_projectId, default)).ReturnsAsync(project);

        var result = await TaskCreationPolicy.ValidateAndBuildAsync(Input(), _projects.Object, _access.Object, default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
        result.ErrorMessage.Should().Contain("paused");
    }

    [Fact]
    public async Task Fails_with_forbidden_when_the_actor_cannot_access_the_project()
    {
        var project = Project.Create("Proj");
        _projects.Setup(p => p.GetByIdAsync(_projectId, default)).ReturnsAsync(project);
        _access.Setup(a => a.CanAccessProjectAsync(_projectId, _actorId, Roles.Engineer, default)).ReturnsAsync(false);

        var result = await TaskCreationPolicy.ValidateAndBuildAsync(Input(), _projects.Object, _access.Object, default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
    }

    [Fact]
    public async Task Fails_when_priority_is_set_without_a_due_date()
    {
        var project = Project.Create("Proj");
        _projects.Setup(p => p.GetByIdAsync(_projectId, default)).ReturnsAsync(project);
        _access.Setup(a => a.CanAccessProjectAsync(_projectId, _actorId, Roles.Engineer, default)).ReturnsAsync(true);

        var result = await TaskCreationPolicy.ValidateAndBuildAsync(Input(priority: 2), _projects.Object, _access.Object, default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
        result.ErrorMessage.Should().Contain("due date");
    }

    [Fact]
    public async Task Builds_a_bare_backlog_task_with_no_assignee_points_or_priority()
    {
        var project = Project.Create("Proj");
        _projects.Setup(p => p.GetByIdAsync(_projectId, default)).ReturnsAsync(project);
        _access.Setup(a => a.CanAccessProjectAsync(_projectId, _actorId, Roles.Engineer, default)).ReturnsAsync(true);

        var result = await TaskCreationPolicy.ValidateAndBuildAsync(Input(), _projects.Object, _access.Object, default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Status.Should().Be(Pulse.Domain.Tasks.TaskStatus.Backlog);
    }

    [Fact]
    public async Task Builds_a_task_with_points_due_date_and_priority_set()
    {
        var project = Project.Create("Proj");
        _projects.Setup(p => p.GetByIdAsync(_projectId, default)).ReturnsAsync(project);
        _access.Setup(a => a.CanAccessProjectAsync(_projectId, _actorId, Roles.Engineer, default)).ReturnsAsync(true);

        var result = await TaskCreationPolicy.ValidateAndBuildAsync(
            Input(points: 5, dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)), priority: 3),
            _projects.Object, _access.Object, default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Points.Should().Be(5);
        result.Data.Priority.Should().Be(3);
    }
}
