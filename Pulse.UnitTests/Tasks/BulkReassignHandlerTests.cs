using Pulse.Application.Common;
using Pulse.Application.Common.Interfaces;
using Pulse.Application.Overwork;
using Pulse.Application.Tasks.Commands;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using Pulse.Domain.Teams;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Tasks;

public class BulkReassignHandlerTests
{
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<IOverworkOverrideRepository> _overrides = new();
    private readonly Mock<IAuditLogRepository> _audit = new();
    private readonly Mock<IEscalationEventRepository> _escalationEvents = new();
    private readonly Mock<IProjectAccessPolicy> _access = new();
    private readonly Mock<IProjectRepository> _projects = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly OverworkSignalsCalculator _calculator = new(new OverworkThresholds());

    public BulkReassignHandlerTests()
    {
        _access
            .Setup(a => a.CanAccessProjectAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _overrides
            .Setup(o => o.GetActiveAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Pulse.Domain.Overrides.OverworkOverride?)null);
    }

    private BulkReassignHandler CreateHandler() =>
        new(_tasks.Object, _engineers.Object, _overrides.Object, _audit.Object, _escalationEvents.Object,
            _calculator, _access.Object, _projects.Object, _teams.Object);

    /// <summary>Builds a QA task and stubs its parent lookup. discipline=null exercises the new
    /// no-discipline role/department restriction; a real discipline exercises the plain IsQa rule.</summary>
    private PulseTask QaTaskWithParent(Discipline? discipline)
    {
        var parent = PulseTask.Create("Ship the thing", 5, Guid.NewGuid(),
            dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)));
        if (discipline.HasValue) parent.SetDiscipline(discipline.Value);

        var qaTask = PulseTask.Create("[QA] Ship the thing", 5, parent.ProjectId,
            dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)));
        qaTask.SetParentTaskId(parent.Id);

        _tasks.Setup(r => r.GetByIdAsync(qaTask.Id, default)).ReturnsAsync(qaTask);
        _tasks.Setup(r => r.GetByIdAsync(parent.Id, default)).ReturnsAsync(parent);
        return qaTask;
    }

    private void SetupEngineer(Engineer engineer) =>
        _engineers.Setup(e => e.GetWithActiveTasksAsync(engineer.Id, default))
            .ReturnsAsync((engineer, Array.Empty<PulseTask>()));

    [Fact]
    public async Task Rejects_reassigning_a_QA_task_to_a_non_QA_engineer()
    {
        // Parent has a discipline set, so this only exercises the plain IsQa requirement —
        // the no-discipline role/department restriction is covered separately below.
        var qaTask = QaTaskWithParent(Discipline.Backend);

        var nonQaEngineer = Engineer.Create("Not QA", "notqa@test.io", "hash", Roles.Engineer, 20, 14);
        _engineers.Setup(e => e.GetByIdAsync(nonQaEngineer.Id, default)).ReturnsAsync(nonQaEngineer);
        SetupEngineer(nonQaEngineer);

        var result = await CreateHandler().Handle(
            new BulkReassignCommand([qaTask.Id], nonQaEngineer.Id, Guid.NewGuid(), null), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
        qaTask.AssigneeId.Should().BeNull("the rejected reassignment must not have applied");
    }

    [Fact]
    public async Task Allows_reassigning_a_QA_task_to_a_QA_engineer()
    {
        var qaTask = QaTaskWithParent(Discipline.Backend);

        var qaEngineer = Engineer.Create("QA Reviewer", "qa@test.io", "hash", Roles.Engineer, 20, 14);
        qaEngineer.SetIsQa(true);
        _engineers.Setup(e => e.GetByIdAsync(qaEngineer.Id, default)).ReturnsAsync(qaEngineer);
        SetupEngineer(qaEngineer);

        var result = await CreateHandler().Handle(
            new BulkReassignCommand([qaTask.Id], qaEngineer.Id, Guid.NewGuid(), null), default);

        result.IsSuccess.Should().BeTrue();
        qaTask.AssigneeId.Should().Be(qaEngineer.Id);
    }

    [Fact]
    public async Task Rejects_assigning_a_no_discipline_QA_task_when_actor_is_not_an_allowed_head()
    {
        var qaTask = QaTaskWithParent(discipline: null);

        var qaEngineer = Engineer.Create("QA Reviewer", "qa@test.io", "hash", Roles.Engineer, 20, 14);
        qaEngineer.SetIsQa(true);
        _engineers.Setup(e => e.GetByIdAsync(qaEngineer.Id, default)).ReturnsAsync(qaEngineer);
        SetupEngineer(qaEngineer);

        var result = await CreateHandler().Handle(
            new BulkReassignCommand([qaTask.Id], qaEngineer.Id, Guid.NewGuid(), null, Roles.ProjectManager), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("FORBIDDEN");
    }

    [Fact]
    public async Task Rejects_assigning_a_no_discipline_QA_task_to_an_engineer_outside_Product_or_Functional()
    {
        var qaTask = QaTaskWithParent(discipline: null);

        var team = Team.Create("Platform", department: "Engineering");
        _teams.Setup(t => t.GetByIdAsync(team.Id, default)).ReturnsAsync(team);

        var qaEngineer = Engineer.Create("QA Reviewer", "qa@test.io", "hash", Roles.Engineer, 20, 14);
        qaEngineer.SetIsQa(true);
        qaEngineer.AssignToTeam(team.Id);
        _engineers.Setup(e => e.GetByIdAsync(qaEngineer.Id, default)).ReturnsAsync(qaEngineer);
        SetupEngineer(qaEngineer);

        var result = await CreateHandler().Handle(
            new BulkReassignCommand([qaTask.Id], qaEngineer.Id, Guid.NewGuid(), null, Roles.HeadOfPmo), default);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("BUSINESS_RULE_VIOLATION");
    }

    [Fact]
    public async Task Allows_HeadOfPmo_to_assign_a_no_discipline_QA_task_to_a_Product_department_engineer()
    {
        var qaTask = QaTaskWithParent(discipline: null);

        var team = Team.Create("Product Team", department: "Product");
        _teams.Setup(t => t.GetByIdAsync(team.Id, default)).ReturnsAsync(team);

        var qaEngineer = Engineer.Create("QA Reviewer", "qa@test.io", "hash", Roles.Engineer, 20, 14);
        qaEngineer.SetIsQa(true);
        qaEngineer.AssignToTeam(team.Id);
        _engineers.Setup(e => e.GetByIdAsync(qaEngineer.Id, default)).ReturnsAsync(qaEngineer);
        SetupEngineer(qaEngineer);

        var result = await CreateHandler().Handle(
            new BulkReassignCommand([qaTask.Id], qaEngineer.Id, Guid.NewGuid(), null, Roles.HeadOfPmo), default);

        result.IsSuccess.Should().BeTrue();
        qaTask.AssigneeId.Should().Be(qaEngineer.Id);
    }
}
