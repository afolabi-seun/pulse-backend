using Pulse.Application.Common.Interfaces;
using Pulse.Application.Overwork;
using Pulse.Application.Overwork.Queries;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using Pulse.Domain.Teams;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Overwork;

public class GetEngineerSignalsHandlerTests
{
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<IOverworkOverrideRepository> _overrides = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<IDepartmentThresholdRepository> _departmentThresholds = new();
    private readonly OverworkSignalsCalculator _calculator = new(new OverworkThresholds { LoadVsBaselineRatio = 1.3 });

    private GetEngineerSignalsHandler CreateHandler() =>
        new(_engineers.Object, _overrides.Object, _teams.Object, _departmentThresholds.Object, _calculator);

    // PulseTask caps a single task at 13 points (the story-point scale), so a test wanting a
    // load total above that spreads it across multiple tasks rather than one oversized one — same
    // total, same effect on the load-vs-baseline sum this handler computes.
    private static PulseTask[] TasksTotaling(int totalPoints)
    {
        var tasks = new List<PulseTask>();
        var remaining = totalPoints;
        while (remaining > 0)
        {
            var chunk = Math.Min(remaining, 13);
            tasks.Add(PulseTask.Create("Task", chunk, Guid.NewGuid(), dueDate: DateOnly.FromDateTime(DateTime.UtcNow)));
            remaining -= chunk;
        }
        return tasks.ToArray();
    }

    private Engineer SetUpEngineerOnTeam(string department, int points)
    {
        var team = Team.Create("Payments", Guid.NewGuid(), department);
        var engineer = Engineer.Create("Dev", "dev@pulse.io", "pw", Roles.Engineer, 20, 14);
        engineer.AssignToTeam(team.Id);

        var tasks = TasksTotaling(points);

        _engineers.Setup(e => e.GetByIdAsync(engineer.Id, default)).ReturnsAsync(engineer);
        _engineers.Setup(e => e.GetWithActiveTasksAsync(engineer.Id, default))
            .ReturnsAsync((engineer, (IReadOnlyList<PulseTask>)tasks));
        _overrides.Setup(o => o.GetActiveAsync(engineer.Id, default)).ReturnsAsync((Domain.Overrides.OverworkOverride?)null);
        _teams.Setup(t => t.GetByIdAsync(team.Id, default)).ReturnsAsync(team);

        return engineer;
    }

    [Fact]
    public async Task Applies_the_engineers_department_override_when_one_exists()
    {
        // 27 pts > 26 (global ratio 1.3 x 20 baseline) would normally trip, but the department
        // override raises the ratio to 2.0 -> threshold 40.
        var engineer = SetUpEngineerOnTeam("Core Banking", points: 27);
        _departmentThresholds.Setup(d => d.GetByDepartmentAsync("Core Banking", default))
            .ReturnsAsync(new DepartmentThresholdOverride { Department = "Core Banking", LoadVsBaselineRatio = 2.0 });

        var result = await CreateHandler().Handle(new GetEngineerSignalsQuery(engineer.Id), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.LoadVsBaseline.Tripped.Should().BeFalse();
    }

    [Fact]
    public async Task Falls_back_to_global_when_the_engineers_department_has_no_override()
    {
        var engineer = SetUpEngineerOnTeam("Engineering", points: 27);
        _departmentThresholds.Setup(d => d.GetByDepartmentAsync("Engineering", default))
            .ReturnsAsync((DepartmentThresholdOverride?)null);

        var result = await CreateHandler().Handle(new GetEngineerSignalsQuery(engineer.Id), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.LoadVsBaseline.Tripped.Should().BeTrue();
    }

    [Fact]
    public async Task Skips_the_team_lookup_entirely_when_engineer_has_no_team()
    {
        var engineer = Engineer.Create("Solo", "solo@pulse.io", "pw", Roles.Engineer, 20, 14);
        var tasks = TasksTotaling(27);
        _engineers.Setup(e => e.GetByIdAsync(engineer.Id, default)).ReturnsAsync(engineer);
        _engineers.Setup(e => e.GetWithActiveTasksAsync(engineer.Id, default))
            .ReturnsAsync((engineer, (IReadOnlyList<PulseTask>)tasks));
        _overrides.Setup(o => o.GetActiveAsync(engineer.Id, default)).ReturnsAsync((Domain.Overrides.OverworkOverride?)null);

        var result = await CreateHandler().Handle(new GetEngineerSignalsQuery(engineer.Id), default);

        result.IsSuccess.Should().BeTrue();
        _teams.Verify(t => t.GetByIdAsync(It.IsAny<Guid>(), default), Times.Never);
        _departmentThresholds.Verify(d => d.GetByDepartmentAsync(It.IsAny<string>(), default), Times.Never);
    }
}
