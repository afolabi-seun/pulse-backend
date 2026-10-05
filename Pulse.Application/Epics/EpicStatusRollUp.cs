using Pulse.Application.Common.Interfaces;
using Pulse.Domain.Epics;

namespace Pulse.Application.Epics;

internal static class EpicStatusRollUp
{
    internal static async Task ApplyAsync(Guid? epicId, ITaskRepository tasks, IEpicRepository epics, CancellationToken ct)
    {
        if (!epicId.HasValue) return;

        var progress = await tasks.GetTaskProgressByEpicsAsync([epicId.Value], ct);
        if (!progress.TryGetValue(epicId.Value, out var counts) || counts.Total == 0) return;

        var epic = await epics.GetByIdAsync(epicId.Value, ct);
        if (epic is null) return;

        var target = counts.Completed == counts.Total ? EpicStatus.Done : EpicStatus.InProgress;
        if (epic.Status == target) return;

        epic.SetStatus(target);
        await epics.SaveChangesAsync(ct);
    }
}
