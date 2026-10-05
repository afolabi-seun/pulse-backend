using Pulse.Application.Common.Interfaces;
using Pulse.Domain.CheckIns;

namespace Pulse.Application.CheckIns;

/// <summary>
/// Completing a task doubles as "I did something today" — seeds or extends today's check-in for
/// that task's project from the task just finished, rather than making the engineer separately
/// fill out the check-in form. Scoped per project (not "any check-in today" like the original,
/// one-shot version of this) so it composes with the existing "one check-in per project per day"
/// rule <see cref="Pulse.Application.CheckIns.Commands.SubmitCheckInCommand"/> already
/// enforces — completing three tasks in three different projects in one day produces three
/// check-ins, each showing separately in the standup summary, matching a manually-submitted
/// check-in's own project scoping.
///
/// Finishing one's own stage of a task counts the same way — handing a two-stage task to the next
/// engineer, and sending a task to QA — since those are the moments the work leaves one's hands, and
/// neither is a "Done" that the completion path above would ever see for that engineer.
/// </summary>
public static class AutoCheckIn
{
    public static Task EnsureForTaskCompletionAsync(
        ICheckInRepository checkIns, Guid actorId, Guid projectId, string taskTitle, CancellationToken ct) =>
        EnsureEntryAsync(checkIns, actorId, projectId, $"Completed: {taskTitle}", ct);

    /// <summary>A Backend ↔ Frontend hand-off — <paramref name="targetStage"/> is the stage the task was
    /// handed to ("Frontend" / "Backend").</summary>
    public static Task EnsureForTaskHandoffAsync(
        ICheckInRepository checkIns, Guid engineerId, Guid projectId, string targetStage, string taskTitle, CancellationToken ct) =>
        EnsureEntryAsync(checkIns, engineerId, projectId, $"Handed off to {targetStage}: {taskTitle}", ct);

    public static Task EnsureForSentToQaAsync(
        ICheckInRepository checkIns, Guid engineerId, Guid projectId, string taskTitle, CancellationToken ct) =>
        EnsureEntryAsync(checkIns, engineerId, projectId, $"Sent to QA: {taskTitle}", ct);

    private static async Task EnsureEntryAsync(
        ICheckInRepository checkIns, Guid actorId, Guid projectId, string entry, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var existing = await checkIns.GetByEngineerAndDateAsync(actorId, today, projectId, ct);
        if (existing is not null)
        {
            // Append rather than replace — SubmitCheckInCommand's own same-day resubmission does
            // a full replace because there the caller is re-typing the whole thing; here each
            // call is one more task finishing during the day, so the prior entries (whether from
            // an earlier auto check-in or something the engineer wrote by hand this morning) are
            // additive context that shouldn't be silently lost.
            existing.Update($"{existing.Completed}\n{entry}", existing.PlannedNext, existing.Blockers);
            await checkIns.SaveChangesAsync(ct);
            return;
        }

        var checkIn = CheckIn.Submit(actorId, today, entry, string.Empty, null, projectId);
        await checkIns.AddAsync(checkIn, ct);
        await checkIns.SaveChangesAsync(ct);
    }
}
