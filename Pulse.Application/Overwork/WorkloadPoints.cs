using Pulse.Domain.Tasks;

namespace Pulse.Application.Overwork;

/// <summary>
/// The points a task adds to someone's workload. Normally all of them; with progress counted, only the part not yet earned, so a task that is three
/// quarters done weighs a quarter. The sum is rounded to whole points because that is how workload and the overwork threshold are expressed.
/// </summary>
public static class WorkloadPoints
{
    public static decimal Of(PulseTask task, bool useProgress) =>
        useProgress ? Math.Max(0m, task.Points - task.EarnedPoints) : task.Points;

    public static int Sum(IEnumerable<PulseTask> tasks, bool useProgress) =>
        (int)Math.Round(tasks.Sum(t => Of(t, useProgress)), MidpointRounding.AwayFromZero);
}
