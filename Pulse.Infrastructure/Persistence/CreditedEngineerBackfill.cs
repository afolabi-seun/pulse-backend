namespace Pulse.Infrastructure.Persistence;

/// <summary>The one-time statement behind the BackfillCreditedEngineerId migration, kept here so the
/// migration and its integration test run the exact same SQL.
///
/// Done-transition history rows written before CreditedEngineerId existed carry a null credit, so
/// reports fall back to the task's current assignee — the frontend engineer for a past
/// backend->frontend handoff, the borrower for a past loan. This applies the same priority the live
/// rule uses (PulseTask.MarkDone: LoanedFromEngineerId ?? BackendAssigneeId ?? AssigneeId), and only
/// touches rows that would actually change: still null, credit differing from the current assignee,
/// not a QA sub-task. Tasks with neither field set are left alone — nothing is guessed. Re-running it
/// changes nothing.
///
/// A migration can't be previewed or aborted, so to check the effect against real data BEFORE
/// deploying, run this read-only equivalent as the DB owner role (RLS is on for the runtime role):
///
///   SELECT e.name, COUNT(*) AS rows_that_would_change
///   FROM task_history h
///   JOIN tasks t ON t.id = h.task_id
///   JOIN engineers e ON e.id = COALESCE(t.loaned_from_engineer_id, t.backend_assignee_id)
///   WHERE h.field = 'status' AND h.to_value = 'Done'
///     AND h.credited_engineer_id IS NULL
///     AND t.parent_task_id IS NULL
///     AND COALESCE(t.loaned_from_engineer_id, t.backend_assignee_id) IS DISTINCT FROM t.assignee_id
///   GROUP BY e.name ORDER BY rows_that_would_change DESC;
///
/// Effect: past totals drop for borrowers and frontend engineers and rise for lenders and backend
/// engineers, for any report range that includes those completions.</summary>
public static class CreditedEngineerBackfill
{
    public const string Sql = """
        UPDATE task_history h
        SET credited_engineer_id = COALESCE(t.loaned_from_engineer_id, t.backend_assignee_id)
        FROM tasks t
        WHERE h.task_id = t.id
          AND h.field = 'status' AND h.to_value = 'Done'
          AND h.credited_engineer_id IS NULL
          AND t.parent_task_id IS NULL
          AND COALESCE(t.loaned_from_engineer_id, t.backend_assignee_id) IS NOT NULL
          AND COALESCE(t.loaned_from_engineer_id, t.backend_assignee_id) IS DISTINCT FROM t.assignee_id;
        """;
}
