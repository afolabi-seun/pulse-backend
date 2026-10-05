using Pulse.Application.Common.Interfaces;
using Pulse.Application.Engineers.Queries;
using Pulse.Application.Overwork;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;
using Pulse.Domain.Teams;
using FluentAssertions;
using Moq;

namespace Pulse.UnitTests.Engineers;

public class ListEngineersPageQueryTests
{
    private readonly Mock<IEngineerRepository> _engineers = new();
    private readonly Mock<ITeamRepository> _teams = new();
    private readonly Mock<ITaskRepository> _tasks = new();
    private readonly Mock<IOverworkOverrideRepository> _overrides = new();
    private readonly Mock<IDepartmentThresholdRepository> _departmentThresholds = new();
    private readonly List<PulseTask> _activeTasks = [];

    public ListEngineersPageQueryTests()
    {
        _tasks.Setup(r => r.GetAllActiveAsync(default)).ReturnsAsync(() => _activeTasks);
        _overrides.Setup(r => r.GetAllActiveAsync(default)).ReturnsAsync([]);
        _departmentThresholds.Setup(r => r.GetAllAsync(default)).ReturnsAsync([]);
        _teams.Setup(r => r.ListAllAsync(default)).ReturnsAsync([]);
    }

    private ListEngineersPageHandler CreateHandler() => new(
        _engineers.Object, _teams.Object, _tasks.Object,
        new EngineerWorkloadAssessor(new OverworkSignalsCalculator(new OverworkThresholds()), _overrides.Object, _departmentThresholds.Object, _teams.Object));

    /// <summary>Gives the engineer the work that trips the overwork signal: four tasks (over the concurrency limit of 3)
    /// carrying more points than 1.3 × baseline, all due within the cycle. The engineers here have a 5-day cycle.</summary>
    private void MakeOverworked(Engineer engineer, int dueInDays = 3)
    {
        foreach (var points in new[] { 5, 5, 3, 2 })
            _activeTasks.Add(Assigned(engineer, points, dueInDays));
    }

    private static PulseTask Assigned(Engineer engineer, int points, int dueInDays)
    {
        var task = PulseTask.Create("T", points, Guid.NewGuid(), dueDate: DateOnly.FromDateTime(DateTime.UtcNow.AddDays(dueInDays)));
        task.Assign(engineer.Id, Guid.NewGuid());
        return task;
    }

    private static Engineer MakeEngineer(string name, int baselinePoints = 10, bool isActive = true)
    {
        var e = Engineer.Create(name, $"{name.ToLower()}@ex.com", "hash", Roles.Engineer, baselinePoints, 5);
        if (!isActive) e.Deactivate();
        return e;
    }

    [Fact]
    public async Task Stats_reflect_the_full_scoped_roster_even_when_the_grid_is_filtered_to_one_team()
    {
        var teamA = Team.Create("Alpha");
        var teamB = Team.Create("Beta");
        var engA = MakeEngineer("A", baselinePoints: 10);
        engA.AssignToTeam(teamA.Id);
        var engB = MakeEngineer("B", baselinePoints: 10);
        engB.AssignToTeam(teamB.Id);

        // engA is overworked (15 points due this cycle, four tasks, baseline 10); engB is not.
        MakeOverworked(engA);
        _activeTasks.Add(Assigned(engB, 5, 3));
        _engineers.Setup(r => r.ListWithWorkloadAsync(null, null, default))
            .ReturnsAsync(new[] { (engA, 2, 15), (engB, 1, 5) });

        var result = await CreateHandler().Handle(
            new ListEngineersPageQuery(Roles.HeadOfPmo, Guid.NewGuid(), TeamId: teamA.Id), default);

        result.IsSuccess.Should().BeTrue();
        // Grid narrowed to team A, but stats still cover both engineers.
        result.Data!.Items.Should().ContainSingle(e => e.Id == engA.Id);
        result.Data!.Stats.ActiveCount.Should().Be(2);
        result.Data!.Stats.OverworkedCount.Should().Be(1);
        result.Data!.Stats.TasksInFlight.Should().Be(3);
    }

    [Fact]
    public async Task Paginates_the_filtered_rows_and_reports_hasMore()
    {
        var engineers = Enumerable.Range(0, 5).Select(i => (MakeEngineer($"E{i}"), 0, 0)).ToArray();
        _engineers.Setup(r => r.ListWithWorkloadAsync(null, null, default)).ReturnsAsync(engineers);

        var page1 = await CreateHandler().Handle(
            new ListEngineersPageQuery(Roles.HeadOfPmo, Guid.NewGuid(), Limit: 2), default);

        page1.IsSuccess.Should().BeTrue();
        page1.Data!.Items.Should().HaveCount(2);
        page1.Data!.HasMore.Should().BeTrue();
        page1.Data!.NextCursor.Should().NotBeNull();

        var page2 = await CreateHandler().Handle(
            new ListEngineersPageQuery(Roles.HeadOfPmo, Guid.NewGuid(), Limit: 2, Cursor: page1.Data!.NextCursor), default);

        page2.Data!.Items.Should().HaveCount(2);
        // No overlap between pages.
        page2.Data!.Items.Select(e => e.Id).Should().NotIntersectWith(page1.Data!.Items.Select(e => e.Id));
    }

    [Fact]
    public async Task Filters_rows_by_isActive_without_affecting_stats()
    {
        var active   = MakeEngineer("Active", isActive: true);
        var inactive = MakeEngineer("Inactive", isActive: false);
        _engineers.Setup(r => r.ListWithWorkloadAsync(null, null, default))
            .ReturnsAsync(new[] { (active, 0, 0), (inactive, 0, 0) });

        var result = await CreateHandler().Handle(
            new ListEngineersPageQuery(Roles.HeadOfPmo, Guid.NewGuid(), IsActive: false), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Items.Should().ContainSingle(e => e.Id == inactive.Id);
        // Stats always ignore the row filter and only ever count active engineers.
        result.Data!.Stats.ActiveCount.Should().Be(1);
    }

    [Fact]
    public async Task Executive_sees_every_engineer_org_wide()
    {
        var teamA = Team.Create("Alpha");
        var teamB = Team.Create("Beta");
        var engA = MakeEngineer("ExecA"); engA.AssignToTeam(teamA.Id);
        var engB = MakeEngineer("ExecB"); engB.AssignToTeam(teamB.Id);

        _engineers.Setup(r => r.ListWithWorkloadAsync(null, null, default))
            .ReturnsAsync(new[] { (engA, 0, 0), (engB, 0, 0) });

        var result = await CreateHandler().Handle(
            new ListEngineersPageQuery(Roles.Executive, Guid.NewGuid(), Limit: 100), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Items.Select(e => e.Id).Should().BeEquivalentTo(new[] { engA.Id, engB.Id });
    }

    [Fact]
    public async Task Hr_sees_every_engineer_org_wide()
    {
        var teamA = Team.Create("Alpha");
        var teamB = Team.Create("Beta");
        var engA = MakeEngineer("HrA"); engA.AssignToTeam(teamA.Id);
        var engB = MakeEngineer("HrB"); engB.AssignToTeam(teamB.Id);

        _engineers.Setup(r => r.ListWithWorkloadAsync(null, null, default))
            .ReturnsAsync(new[] { (engA, 0, 0), (engB, 0, 0) });

        var result = await CreateHandler().Handle(
            new ListEngineersPageQuery(Roles.HR, Guid.NewGuid(), Limit: 100), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Items.Select(e => e.Id).Should().BeEquivalentTo(new[] { engA.Id, engB.Id });
    }

    [Fact]
    public async Task Excludes_roles_that_can_never_carry_a_delivery_workload_from_rows_and_stats()
    {
        var engineer = MakeEngineer("Dev", baselinePoints: 10);
        var pmo = Engineer.Create("Pmo", "pmo@ex.com", "hash", Roles.HeadOfPmo, 10, 5);
        _engineers.Setup(r => r.ListWithWorkloadAsync(null, null, default))
            .ReturnsAsync(new[] { (engineer, 2, 5), (pmo, 0, 0) });

        var result = await CreateHandler().Handle(
            new ListEngineersPageQuery(Roles.Executive, Guid.NewGuid(), Limit: 100), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Items.Should().ContainSingle(e => e.Id == engineer.Id);
        result.Data.Items.Should().NotContain(e => e.Id == pmo.Id);
        result.Data.Stats.ActiveCount.Should().Be(1);
    }

    [Fact]
    public async Task Department_head_sees_every_team_in_their_department_scoped_correctly()
    {
        var callerId = Guid.NewGuid();
        var ownTeam   = Team.Create("R&D Core", department: "R&D");
        var sisterTeam = Team.Create("R&D Platform", department: "R&D");
        var otherDeptTeam = Team.Create("Design", department: "Design");

        var caller = MakeEngineer("Head");
        caller.AssignToTeam(ownTeam.Id);

        var inDept1 = MakeEngineer("InDept1"); inDept1.AssignToTeam(ownTeam.Id);
        var inDept2 = MakeEngineer("InDept2"); inDept2.AssignToTeam(sisterTeam.Id);
        var outOfDept = MakeEngineer("OutOfDept"); outOfDept.AssignToTeam(otherDeptTeam.Id);

        _engineers.Setup(r => r.GetByIdAsync(callerId, default)).ReturnsAsync(caller);
        _teams.Setup(r => r.GetByIdAsync(ownTeam.Id, default)).ReturnsAsync(ownTeam);
        _teams.Setup(r => r.ListAllAsync(default)).ReturnsAsync(new[] { ownTeam, sisterTeam, otherDeptTeam });
        _engineers.Setup(r => r.ListWithWorkloadAsync(null, null, default))
            .ReturnsAsync(new[] { (caller, 0, 0), (inDept1, 0, 0), (inDept2, 0, 0), (outOfDept, 0, 0) });

        var result = await CreateHandler().Handle(
            new ListEngineersPageQuery(Roles.HeadOfRnD, callerId, Limit: 100), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Items.Select(e => e.Id).Should().BeEquivalentTo(new[] { caller.Id, inDept1.Id, inDept2.Id });
    }

    [Fact]
    public async Task Overworked_follows_the_overwork_signal_not_active_points_over_baseline()
    {
        var farOff = MakeEngineer("FarOff", baselinePoints: 10);
        var thisCycle = MakeEngineer("NearCycle", baselinePoints: 10);
        // FarOff: 15 active points (more than the baseline of 10) but all due in two months, so none of it is this
        // cycle's load — the signal does not flag it, so neither may the page.
        MakeOverworked(farOff, dueInDays: 60);
        _engineers.Setup(r => r.ListWithWorkloadAsync(null, null, default))
            .ReturnsAsync(new[] { (farOff, 4, 15), (thisCycle, 4, 15) });
        MakeOverworked(thisCycle);

        var result = await CreateHandler().Handle(new ListEngineersPageQuery(Roles.HeadOfPmo, Guid.NewGuid()), default);

        result.Data!.Stats.OverworkedCount.Should().Be(1);
        var far = result.Data.Items.Single(e => e.Id == farOff.Id);
        far.TotalPoints.Should().Be(15);
        far.IsOverworked.Should().BeFalse("its points are due long after this cycle");
        far.CyclePoints.Should().Be(0);
        var near = result.Data.Items.Single(e => e.Id == thisCycle.Id);
        near.IsOverworked.Should().BeTrue();
        near.CyclePoints.Should().Be(15);
    }

    [Fact]
    public async Task Applies_the_engineers_department_thresholds_like_the_digest_does()
    {
        var team = Team.Create("Alpha", department: "Engineering");
        var engineer = MakeEngineer("E", baselinePoints: 10);
        engineer.AssignToTeam(team.Id);
        MakeOverworked(engineer);
        _teams.Setup(r => r.ListAllAsync(default)).ReturnsAsync([team]);
        // This department tolerates up to 3x baseline, so 15 points on a baseline of 10 is no longer overload.
        _departmentThresholds.Setup(r => r.GetAllAsync(default)).ReturnsAsync(
            [new DepartmentThresholdOverride { Department = "Engineering", LoadVsBaselineRatio = 3.0, MaxConcurrentTasks = 10 }]);
        _engineers.Setup(r => r.ListWithWorkloadAsync(null, null, default)).ReturnsAsync(new[] { (engineer, 4, 15) });

        var result = await CreateHandler().Handle(new ListEngineersPageQuery(Roles.HeadOfPmo, Guid.NewGuid()), default);

        result.Data!.Items.Single().IsOverworked.Should().BeFalse();
        result.Data.Stats.OverworkedCount.Should().Be(0);
    }


    [Fact]
    public async Task Average_utilisation_is_cycle_load_over_baseline_the_same_measure_as_the_overworked_verdict()
    {
        var a = MakeEngineer("A", baselinePoints: 10);
        var b = MakeEngineer("B", baselinePoints: 10);
        // A: 20 points active but all due in two months -> 0 due this cycle. B: 10 due this cycle -> 100%.
        _activeTasks.Add(Assigned(a, 13, 60));
        _activeTasks.Add(Assigned(a, 7, 60));
        _activeTasks.Add(Assigned(b, 10, 3));
        _engineers.Setup(r => r.ListWithWorkloadAsync(null, null, default)).ReturnsAsync(new[] { (a, 2, 20), (b, 1, 10) });

        var result = await CreateHandler().Handle(new ListEngineersPageQuery(Roles.HeadOfPmo, Guid.NewGuid()), default);

        // (0% + 100%) / 2 — counting A's far-off 20 points would have given 150%.
        result.Data!.Stats.AvgUtilisation.Should().Be(50);
    }
}
