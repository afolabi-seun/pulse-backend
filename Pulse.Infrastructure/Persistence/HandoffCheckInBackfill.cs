namespace Pulse.Infrastructure.Persistence;

/// <summary>The one-time statement behind the BackfillHandoffCheckIns migration, kept here so the
/// migration and its integration test run the exact same SQL.
///
/// Until AutoCheckIn learned about them, a Backend ↔ Frontend hand-off and a Send-to-QA left nothing
/// in anyone's daily check-in. This replays task history to add the entries that would have been
/// written live:
///
///   * "Handed off to Frontend: &lt;title&gt;" / "Handed off to Backend: &lt;title&gt;" — for the
///     engineer the task was handed FROM (the paired assignee_id row's from_value; the actor if it had
///     no assignee), from each "current_stage" history row.
///   * "Sent to QA: &lt;title&gt;" — for whoever held the task at that moment (the latest assignee_id
///     row at or before the send; the actor if none), from each status -> InQa row of a non-QA task.
///
/// Entries are grouped per engineer, project and UTC day and appended to that day's check-in, or a new
/// check-in is created (submitted_at = the last event's time, empty "planned"). Re-running changes
/// nothing: an entry already present as a whole line of the day's check-in — whether backfilled or
/// written live since the feature shipped — is skipped. Engineers that no longer exist are skipped.
/// work_completed is capped at 2000 characters, so a day with an unusual number of events is cut at
/// that limit rather than failing.
///
/// Effect to know about: these check-ins count toward the existing check-in measures for the past
/// dates they land on (compliance, streaks, "check-ins this week"), so past figures can rise.
///
/// A migration can't be previewed or aborted, so to see what it would add BEFORE deploying, run this
/// read-only equivalent as the DB owner role:
///
///   SELECT e.name, COUNT(*) AS entries_that_would_be_added
///   FROM task_history h
///   JOIN tasks t ON t.id = h.task_id
///   JOIN engineers e ON e.id = COALESCE(
///       (SELECT NULLIF(a.to_value, '')::uuid FROM task_history a
///         WHERE a.task_id = h.task_id AND a.field = 'assignee_id' AND a.ts &lt;= h.ts
///         ORDER BY a.ts DESC LIMIT 1), h.actor_id)
///   WHERE h.field = 'status' AND h.to_value = 'InQa' AND t.parent_task_id IS NULL
///   GROUP BY e.name ORDER BY entries_that_would_be_added DESC;
///
/// (That shows the Send-to-QA half; the hand-off half is the same shape over field = 'current_stage'.)</summary>
public static class HandoffCheckInBackfill
{
    public const string Sql = """
        WITH handoffs AS (
            SELECT t.project_id, h.ts,
                   COALESCE(
                       (SELECT NULLIF(a.from_value, '')::uuid
                          FROM task_history a
                         WHERE a.task_id = h.task_id AND a.field = 'assignee_id'
                           AND a.actor_id = h.actor_id
                           AND a.ts BETWEEN h.ts - interval '5 seconds' AND h.ts + interval '5 seconds'
                         ORDER BY abs(extract(epoch FROM (a.ts - h.ts))) LIMIT 1),
                       h.actor_id) AS engineer_id,
                   'Handed off to ' || h.to_value || ': ' || t.title AS entry
              FROM task_history h
              JOIN tasks t ON t.id = h.task_id
             WHERE h.field = 'current_stage'
               AND h.from_value IN ('Backend', 'Frontend')
               AND h.to_value   IN ('Backend', 'Frontend')
               AND h.from_value <> h.to_value
        ),
        sent_to_qa AS (
            SELECT t.project_id, h.ts,
                   COALESCE(
                       (SELECT NULLIF(a.to_value, '')::uuid
                          FROM task_history a
                         WHERE a.task_id = h.task_id AND a.field = 'assignee_id' AND a.ts <= h.ts
                         ORDER BY a.ts DESC LIMIT 1),
                       h.actor_id) AS engineer_id,
                   'Sent to QA: ' || t.title AS entry
              FROM task_history h
              JOIN tasks t ON t.id = h.task_id
             WHERE h.field = 'status' AND h.to_value = 'InQa'
               AND t.parent_task_id IS NULL
        ),
        located AS (
            SELECT ev.engineer_id, ev.project_id, (ev.ts AT TIME ZONE 'UTC')::date AS day, ev.ts, ev.entry
              FROM (SELECT * FROM handoffs UNION ALL SELECT * FROM sent_to_qa) ev
              JOIN engineers e ON e.id = ev.engineer_id
        ),
        deduped AS (
            SELECT DISTINCT ON (engineer_id, project_id, day, entry)
                   engineer_id, project_id, day, ts, entry
              FROM located
             ORDER BY engineer_id, project_id, day, entry, ts
        ),
        fresh AS (
            SELECT d.*
              FROM deduped d
              LEFT JOIN check_ins c
                     ON c.engineer_id = d.engineer_id AND c.project_id = d.project_id AND c.date = d.day
             WHERE c.id IS NULL
                OR position(E'\n' || d.entry || E'\n' IN E'\n' || c.work_completed || E'\n') = 0
        ),
        grouped AS (
            SELECT engineer_id, project_id, day,
                   string_agg(entry, E'\n' ORDER BY ts) AS entries,
                   max(ts) AS last_ts
              FROM fresh
             GROUP BY engineer_id, project_id, day
        )
        INSERT INTO check_ins (id, engineer_id, date, work_completed, planned, project_id, submitted_at)
        SELECT gen_random_uuid(), engineer_id, day, left(entries, 2000), '', project_id, last_ts
          FROM grouped
        ON CONFLICT (engineer_id, date, project_id) DO UPDATE
           SET work_completed = left(check_ins.work_completed || E'\n' || EXCLUDED.work_completed, 2000);
        """;
}
