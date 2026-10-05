using Pulse.Application.Overwork;
using Pulse.Domain.Engineers;
using Pulse.Domain.Tasks;

namespace Pulse.Application.Reports;

/// <summary>Builds one engineer's <see cref="EngineerUtilizationEntry"/> — extracted because
/// GetPmoReportHandler and WeeklyReportAssembler built this identically (both already share this
/// DTO), so a third near-duplicate wasn't worth adding when the InQa-visibility and
/// subtask-completion fields were introduced.</summary>
public static class UtilizationEntryBuilder
{
    public static EngineerUtilizationEntry Build(
        Engineer engineer,
        IReadOnlyDictionary<Guid, IReadOnlyList<PulseTask>> tasksByAssignee,
        IReadOnlyDictionary<Guid, IReadOnlyList<PulseTask>> inQaTasksByAssignee,
        IReadOnlyDictionary<Guid, int> checkInCounts,
        IReadOnlyDictionary<Guid, int> blockersByAssignee,
        IReadOnlyDictionary<Guid, decimal> hoursThisWeek,
        IReadOnlyDictionary<Guid, int> completedTasksByEngineer,
        IReadOnlyDictionary<Guid, int> subtasksCompletedByEngineer,
        IReadOnlyDictionary<Guid, EngineerWorkload> workloadByEngineer)
    {
        var tasks = tasksByAssignee.TryGetValue(engineer.Id, out var t) ? t : Array.Empty<PulseTask>();
        var inQaTasks = inQaTasksByAssignee.TryGetValue(engineer.Id, out var q) ? q : Array.Empty<PulseTask>();
        // Who is overworked, and how much is due this cycle, is decided in one place — see EngineerWorkloadAssessor.
        var workload = workloadByEngineer.GetValueOrDefault(engineer.Id) ?? new EngineerWorkload(tasks.Count, tasks.Sum(x => x.Points), 0, false);
        checkInCounts.TryGetValue(engineer.Id, out var ciCount);
        blockersByAssignee.TryGetValue(engineer.Id, out var bCount);
        hoursThisWeek.TryGetValue(engineer.Id, out var hrs);
        completedTasksByEngineer.TryGetValue(engineer.Id, out var completedCount);
        subtasksCompletedByEngineer.TryGetValue(engineer.Id, out var subtasksCount);

        return new EngineerUtilizationEntry(
            engineer.Id, engineer.Name, engineer.Role,
            tasks.Count, tasks.Sum(x => x.Points), engineer.BaselinePoints,
            workload.IsOverworked, ciCount, bCount, hrs, completedCount,
            inQaTasks.Count, inQaTasks.Sum(x => x.Points), subtasksCount, workload.CyclePoints);
    }
}
