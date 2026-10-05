using Pulse.Application.Common.Interfaces;
using Pulse.Application.Overwork;
using Pulse.Application.Reports.Queries;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using Pulse.Domain.Teams;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Reports;

public class GetLeadershipReportQueryTests
{
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<ICheckInRepository> _checkIns = new();
    private readonly Mock<IOverworkOverrideRepository> _overrides = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<IProjectRepository> _projects = new();
    private readonly Mock<ITimeEntryRepository> _timeEntries = new();
    private readonly OverworkSignalsCalculator _calculator = new(new OverworkThresholds());
    private readonly Mock<IDepartmentThresholdRepository> _departmentThresholds = new();

    private EngineerWorkloadAssessor Assessor =>
        new(_calculator, _overrides.Object, _departmentThresholds.Object, _teams.Object);
    private readonly OverworkThresholds _thresholds = new();

    private GetLeadershipReportHandler CreateHandler() => new(
        _engineers.Object, _tasks.Object, _checkIns.Object, Assessor, _thresholds, _teams.Object, _projects.Object, _timeEntries.Object);

    private void SetupMinimalData(IReadOnlyList<Engineer> engineers)
    {
        _engineers.Setup(r => r.ListActiveAsync(default)).ReturnsAsync(engineers);
        _tasks.Setup(r => r.GetAllActiveAsync(default)).ReturnsAsync(Array.Empty<PulseTask>());
        _tasks.Setup(r => r.GetBlockedTasksAsync(default)).ReturnsAsync(Array.Empty<PulseTask>());
        _overrides.Setup(r => r.GetAllActiveAsync(default)).ReturnsAsync([]);
        _departmentThresholds.Setup(r => r.GetAllAsync(default)).ReturnsAsync([]);
        _checkIns.Setup(r => r.GetCheckInCountByDateRangeAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), default))
            .ReturnsAsync(new Dictionary<Guid, int>());
        _tasks.Setup(r => r.GetEscalationCandidatesAsync(It.IsAny<int>(), default)).ReturnsAsync(Array.Empty<PulseTask>());
        _tasks.Setup(r => r.GetDeliveredPointsInRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), default)).ReturnsAsync(0);
        _tasks.Setup(r => r.GetSubtaskCompletionCountByEngineerInRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), default))
            .ReturnsAsync(new Dictionary<Guid, int>());
        _tasks.Setup(r => r.GetCompletedTaskCountByEngineerInRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), default))
            .ReturnsAsync(new Dictionary<Guid, int>());
        _timeEntries.Setup(r => r.GetDeliveryHoursByEngineerInRangeAsync(It.IsAny<DateOnly>(), It.IsAny<DateOnly>(), It.IsAny<IReadOnlyList<Guid>>(), default))
            .ReturnsAsync(new Dictionary<Guid, decimal>());
        _projects.Setup(r => r.GetNamesByIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), default))
            .ReturnsAsync(new Dictionary<Guid, string>());
        _projects.Setup(r => r.GetCodesByIdsAsync(It.IsAny<IReadOnlyList<Guid>>(), default))
            .ReturnsAsync(new Dictionary<Guid, string>());
    }

    [Fact]
    public async Task Engineer_entries_exclude_roles_that_can_never_carry_a_delivery_workload()
    {
        var engineer = Engineer.Create("Dev", "dev@x.io", "hash", Roles.Engineer, 20, 14);
        var exec = Engineer.Create("Exec", "exec@x.io", "hash", Roles.Executive, 20, 14);
        var pmo = Engineer.Create("Pmo", "pmo@x.io", "hash", Roles.HeadOfPmo, 20, 14);
        var accountant = Engineer.Create("Acct", "acct@x.io", "hash", Roles.Accountant, 20, 14);
        SetupMinimalData([engineer, exec, pmo, accountant]);

        var result = await CreateHandler().Handle(new GetLeadershipReportQuery(Guid.NewGuid(), Roles.Executive), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Engineers.Should().ContainSingle(e => e.EngineerId == engineer.Id);
        result.Data.Engineers.Should().NotContain(e => e.EngineerId == exec.Id);
        result.Data.Engineers.Should().NotContain(e => e.EngineerId == pmo.Id);
        result.Data.Engineers.Should().NotContain(e => e.EngineerId == accountant.Id);
    }

    [Fact]
    public async Task Engineer_entries_still_include_a_delivery_role()
    {
        var engineer = Engineer.Create("Dev", "dev2@x.io", "hash", Roles.Engineer, 20, 14);
        SetupMinimalData([engineer]);

        var result = await CreateHandler().Handle(new GetLeadershipReportQuery(Guid.NewGuid(), Roles.HR), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Engineers.Should().ContainSingle(e => e.EngineerId == engineer.Id);
    }

    [Fact]
    public async Task IsCallerDepartmentScoped_is_true_for_a_department_head()
    {
        // Delivered points stay org-wide aggregate velocity even for a department-scoped caller
        // (deliberate — see the handler's own comment), so the frontend needs this flag to label
        // that figure "(org-wide)" rather than let it look like it matches the department-scoped
        // Engineers/Escalations/Blockers sitting right below it.
        var team = Team.Create("R&D Team");
        team.SetDepartment("Engineering");
        var head = Engineer.Create("Head", "head@x.io", "hash", Roles.HeadOfRnD, 20, 14);
        head.AssignToTeam(team.Id);

        SetupMinimalData([head]);
        _engineers.Setup(r => r.GetByIdAsync(head.Id, default)).ReturnsAsync(head);
        _engineers.Setup(r => r.ListAllAsync(default)).ReturnsAsync([head]);
        _teams.Setup(r => r.GetByIdAsync(team.Id, default)).ReturnsAsync(team);
        _teams.Setup(r => r.ListAllAsync(default)).ReturnsAsync([team]);

        var result = await CreateHandler().Handle(new GetLeadershipReportQuery(head.Id, Roles.HeadOfRnD), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.IsCallerDepartmentScoped.Should().BeTrue();
    }

    [Theory]
    [InlineData(Roles.Executive)]
    [InlineData(Roles.HR)]
    [InlineData(Roles.HeadOfPmo)]
    public async Task IsCallerDepartmentScoped_is_false_for_an_org_wide_caller(string role)
    {
        SetupMinimalData([]);

        var result = await CreateHandler().Handle(new GetLeadershipReportQuery(Guid.NewGuid(), role), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.IsCallerDepartmentScoped.Should().BeFalse();
    }
}
