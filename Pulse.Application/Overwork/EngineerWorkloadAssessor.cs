using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;

namespace Pulse.Application.Overwork;

/// <summary>One engineer's load, measured the one way the product measures it.</summary>
/// <param name="ActiveTasks">Active and blocked tasks the engineer owns.</param>
/// <param name="ActivePoints">Points on all of those tasks, however far off they are due.</param>
/// <param name="CyclePoints">The part of that due within the engineer's baseline cycle (plus anything overdue or undated)
/// — what the overwork signal compares with the baseline.</param>
/// <param name="IsOverworked">The overwork signal's verdict: enough signals tripped, after the engineer's department
/// thresholds and any active override.</param>
public record EngineerWorkload(int ActiveTasks, int ActivePoints, int CyclePoints, bool IsOverworked);

/// <summary>
/// The single place that says who is overworked and how much load each engineer carries. Every screen and report that
/// shows load or an "overworked" badge goes through here, so the engineer's own dashboard, the Engineers roster, the
/// team and leadership reports and the daily digest cannot disagree. It applies each engineer's department thresholds
/// and any active override, which the reports used not to.
/// </summary>
public class EngineerWorkloadAssessor
{
    private readonly OverworkSignalsCalculator _calculator;
    private readonly IOverworkOverrideRepository _overrides;
    private readonly IDepartmentThresholdRepository _departmentThresholds;
    private readonly ITeamRepository _teams;

    public EngineerWorkloadAssessor(
        OverworkSignalsCalculator calculator,
        IOverworkOverrideRepository overrides,
        IDepartmentThresholdRepository departmentThresholds,
        ITeamRepository teams)
    {
        _calculator = calculator;
        _overrides = overrides;
        _departmentThresholds = departmentThresholds;
        _teams = teams;
    }

    /// <param name="tasks">Any tasks; only each engineer's active and blocked ones are counted.</param>
    public async Task<IReadOnlyDictionary<Guid, EngineerWorkload>> AssessAsync(
        IEnumerable<Engineer> engineers, IEnumerable<PulseTask> tasks, CancellationToken ct = default)
    {
        var overrideByEngineer = (await _overrides.GetAllActiveAsync(ct) ?? [])
            .GroupBy(o => o.EngineerId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(o => o.ExpiresAt).First());
        var departmentOverrides = (await _departmentThresholds.GetAllAsync(ct) ?? []).ToDictionary(d => d.Department);
        var departmentByTeam = (await _teams.ListAllAsync(ct) ?? [])
            .Where(t => t.Department is not null)
            .ToDictionary(t => t.Id, t => t.Department!);

        var workTasksByAssignee = tasks
            .Where(t => t.AssigneeId.HasValue && t.Status.CountsAsActiveWorkload())
            .GroupBy(t => t.AssigneeId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<PulseTask>)g.ToList());

        var result = new Dictionary<Guid, EngineerWorkload>();
        foreach (var engineer in engineers)
        {
            var work = workTasksByAssignee.GetValueOrDefault(engineer.Id) ?? [];
            overrideByEngineer.TryGetValue(engineer.Id, out var activeOverride);

            DepartmentThresholdOverride? departmentOverride = null;
            if (engineer.TeamId is Guid teamId && departmentByTeam.TryGetValue(teamId, out var department))
                departmentOverrides.TryGetValue(department, out departmentOverride);

            var (_, isOverworked) = _calculator.Compute(engineer, work, activeOverride, departmentOverride);
            result[engineer.Id] = new EngineerWorkload(
                work.Count, work.Sum(t => t.Points), OverworkSignalsCalculator.CyclePoints(engineer, work), isOverworked);
        }
        return result;
    }
}
