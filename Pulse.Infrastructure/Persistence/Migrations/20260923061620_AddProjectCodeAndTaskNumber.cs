using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Adds Project.Code (a short, unique, uppercase key, e.g. "NOTIF") and PulseTask.TaskNumber
    /// (sequential per project) — combined into each task's new display key, e.g. "NOTIF-011",
    /// shown wherever a task's title already shows. Also adds PulseTask.ExternalReference, a free
    /// text pointer to a pre-existing ID in another system (Jira, a legacy tracker, …).
    ///
    /// Both new NOT NULL columns start out empty/zero for existing rows (see the AddColumn calls
    /// below), so this backfills them before the unique indexes are created — those would fail to
    /// create against duplicate ''/0 values otherwise:
    ///   - Every existing project gets a code derived from its name (uppercase letters/digits,
    ///     first 6 — same derivation `ProjectCodeGenerator.DeriveBase` uses for new projects),
    ///     de-duplicated with a numeric suffix for projects that derive the same base code.
    ///   - Every existing task gets a task_number sequential within its own project, ordered by
    ///     created_at (oldest first) — matching how a real project's tasks would have been numbered
    ///     had this feature existed from day one.
    ///
    /// The dedup above partitions by base code, so it's exact for the ordinary case; a genuinely
    /// pathological existing dataset (e.g. one project already literally named to collide with
    /// another project's auto-suffixed code) could still hit the unique index and fail this
    /// migration loudly rather than silently — acceptable for a realistic project count, and far
    /// safer than not checking at all.
    /// </summary>
    public partial class AddProjectCodeAndTaskNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // This is a rolling deploy: the outgoing app version can still be inserting tasks/
            // projects while this migration runs, with no idea task_number/code exist yet — those
            // rows land with the plain column defaults (0 / ''). Without a lock, such a row could
            // land in the same project as another zero-defaulted row between the backfill below and
            // the unique indexes at the end, and creating those indexes would fail on the resulting
            // duplicate. Locking blocks concurrent writers on these two tables for this migration's
            // duration (milliseconds to a couple of seconds here) rather than letting them race it.
            migrationBuilder.Sql("LOCK TABLE tasks IN EXCLUSIVE MODE;");
            migrationBuilder.Sql("LOCK TABLE projects IN EXCLUSIVE MODE;");

            // Both tables run FORCE ROW LEVEL SECURITY (see AddRowLevelSecurity's doc comment) —
            // added specifically to stop the app's own connection role (the table owner) from
            // getting Postgres's normal owner exemption from RLS. That's correct for runtime
            // traffic, but this migration connects as that same role, so without lifting FORCE here
            // its own backfill queries below would silently see zero rows (not an error — RLS just
            // filters them out), leaving every task_number/code at its column default and guaranteeing
            // a collision the instant the unique indexes try to build. Lifting FORCE restores the
            // ordinary owner exemption for this migration's connection only, for this transaction
            // only; it's restored at the end, and a failure anywhere rolls the whole toggle back too.
            migrationBuilder.Sql("ALTER TABLE tasks NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE projects NO FORCE ROW LEVEL SECURITY;");

            migrationBuilder.AddColumn<string>(
                name: "external_reference",
                table: "tasks",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "task_number",
                table: "tasks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "code",
                table: "projects",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            // Backfill projects.code — see class summary for the derivation/dedup rule.
            migrationBuilder.Sql(@"
                WITH based AS (
                    SELECT id,
                           CASE
                               WHEN upper(regexp_replace(name, '[^a-zA-Z0-9]', '', 'g')) = ''
                               THEN 'PROJ'
                               ELSE substring(upper(regexp_replace(name, '[^a-zA-Z0-9]', '', 'g')) from 1 for 6)
                           END AS base_code
                    FROM projects
                ),
                ranked AS (
                    SELECT id, base_code,
                           row_number() OVER (PARTITION BY base_code ORDER BY id) AS rn
                    FROM based
                )
                UPDATE projects p
                SET code = CASE WHEN r.rn = 1 THEN r.base_code ELSE r.base_code || r.rn::text END
                FROM ranked r
                WHERE p.id = r.id;");

            // Backfill tasks.task_number — sequential per project, oldest task first.
            migrationBuilder.Sql(@"
                WITH ranked AS (
                    SELECT id, row_number() OVER (PARTITION BY project_id ORDER BY created_at, id) AS rn
                    FROM tasks
                )
                UPDATE tasks t
                SET task_number = r.rn
                FROM ranked r
                WHERE t.id = r.id;");

            migrationBuilder.CreateIndex(
                name: "ux_tasks_project_id_task_number",
                table: "tasks",
                columns: new[] { "project_id", "task_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_projects_code",
                table: "projects",
                column: "code",
                unique: true);

            // Restore FORCE — see the comment above where it was lifted.
            migrationBuilder.Sql("ALTER TABLE tasks FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE projects FORCE ROW LEVEL SECURITY;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_tasks_project_id_task_number",
                table: "tasks");

            migrationBuilder.DropIndex(
                name: "ux_projects_code",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "external_reference",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "task_number",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "code",
                table: "projects");
        }
    }
}
