using Pulse.Application.Backlog;
using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Epics;
using Pulse.Domain.Projects;
using Pulse.Domain.Tasks;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Backlog;

public class ImportBacklogHandlerTests
{
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IProjectRepository> _projects = new();
    private readonly Mock<IEpicRepository> _epics = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<IAuditLogRepository> _audit = new();

    public ImportBacklogHandlerTests()
    {
        _teams.Setup(t => t.ListAllAsync(default)).ReturnsAsync(Array.Empty<Domain.Teams.Team>());
        _projects.Setup(p => p.ListActiveAsync(default)).ReturnsAsync(Array.Empty<Project>());
        _projects.Setup(p => p.AddAsync(It.IsAny<Project>(), default)).Returns(Task.CompletedTask);
        _projects.Setup(p => p.SaveChangesAsync(default)).Returns(Task.CompletedTask);
        _projects.Setup(p => p.GetAllCodesAsync(default)).ReturnsAsync(new HashSet<string>());
        _tasks.Setup(t => t.GetNextTaskNumberAsync(It.IsAny<Guid>(), default)).ReturnsAsync(1);
        _epics.Setup(e => e.ListByProjectAsync(It.IsAny<Guid>(), default)).ReturnsAsync(Array.Empty<Epic>());
        _epics.Setup(e => e.AddAsync(It.IsAny<Epic>(), default)).Returns(Task.CompletedTask);
    }

    private ImportBacklogHandler CreateHandler() =>
        new(_tasks.Object, _projects.Object, _epics.Object, _teams.Object, _audit.Object);

    private static ImportBacklogRow Row(string? priority) => new(
        ProjectName: "Alpha", EpicRef: null, EpicName: "Epic One", Feature: null, StoryRef: null,
        Title: "A task", Description: null, AcceptanceCriteria: null, Priority: priority, Type: null,
        StoryPoints: null, Phase: null, Notes: null);

    [Theory]
    [InlineData("must_have", 5)]
    [InlineData("MUST-HAVE", 5)]
    [InlineData("should_have", 4)]
    [InlineData("could_have", 2)]
    [InlineData("wont_have", 1)]
    [InlineData("critical", 5)]
    [InlineData("high", 4)]
    [InlineData("low", 2)]
    [InlineData("P5", 5)]
    [InlineData("3", 3)]
    public async Task Recognized_priority_values_map_onto_the_1_to_5_field(string raw, int expected)
    {
        PulseTask? added = null;
        _tasks.Setup(t => t.AddAsync(It.IsAny<PulseTask>(), default))
            .Callback<PulseTask, CancellationToken>((t, _) => added = t)
            .Returns(Task.CompletedTask);

        var result = await CreateHandler().Handle(new ImportBacklogCommand([Row(raw)], Guid.NewGuid(), null), default);

        result.Data!.Created.Should().Be(1);
        added!.Priority.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("some random text")]
    public async Task Unrecognized_or_blank_priority_leaves_the_task_unprioritized(string? raw)
    {
        PulseTask? added = null;
        _tasks.Setup(t => t.AddAsync(It.IsAny<PulseTask>(), default))
            .Callback<PulseTask, CancellationToken>((t, _) => added = t)
            .Returns(Task.CompletedTask);

        var result = await CreateHandler().Handle(new ImportBacklogCommand([Row(raw)], Guid.NewGuid(), null), default);

        result.Data!.Created.Should().Be(1);
        added!.Priority.Should().BeNull();
    }

    [Fact]
    public async Task Imported_task_description_no_longer_duplicates_the_raw_priority_text()
    {
        PulseTask? added = null;
        _tasks.Setup(t => t.AddAsync(It.IsAny<PulseTask>(), default))
            .Callback<PulseTask, CancellationToken>((t, _) => added = t)
            .Returns(Task.CompletedTask);

        var row = Row("must_have") with { Description = "Some real description." };
        var result = await CreateHandler().Handle(new ImportBacklogCommand([row], Guid.NewGuid(), null), default);

        result.Data!.Created.Should().Be(1);
        added!.Priority.Should().Be(5);
        added.Description.Should().NotContain("Priority:");
    }

    [Theory]
    [InlineData(14)]
    [InlineData(-1)]
    [InlineData(500)]
    public async Task Row_with_story_points_out_of_range_is_rejected(int storyPoints)
    {
        var row = Row(null) with { StoryPoints = storyPoints };

        var result = await CreateHandler().Handle(new ImportBacklogCommand([row], Guid.NewGuid(), null), default);

        result.Data!.Created.Should().Be(0);
        result.Data.Failures.Should().ContainSingle(f => f.Error.Contains("story_points"));
        _tasks.Verify(t => t.AddAsync(It.IsAny<PulseTask>(), default), Times.Never);
    }
}
