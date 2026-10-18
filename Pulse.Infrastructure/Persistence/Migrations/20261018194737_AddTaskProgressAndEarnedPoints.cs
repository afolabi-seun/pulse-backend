using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskProgressAndEarnedPoints : Migration
    {
        /// <summary>The one-off fill-in for finished tasks, public so a test can run it against real rows.</summary>
        public const string BackfillSql = @"
            insert into task_progress_credits (id, task_id, engineer_id, points, progress_after, credited_at, created_at)
            select gen_random_uuid(), t.id, coalesce(h.credited_engineer_id, t.assignee_id), t.points, 100, coalesce(h.ts, t.created_at), now()
            from tasks t
            left join lateral (
                select ts, credited_engineer_id from task_history
                where task_id = t.id and field = 'status' and to_value = 'Done'
                order by ts desc limit 1
            ) h on true
            where t.status = 'Done' and t.parent_task_id is null and t.points > 0
              and not exists (select 1 from task_progress_credits c where c.task_id = t.id);

            update tasks set earned_points = points
            where status = 'Done' and parent_task_id is null;";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "earned_points",
                table: "tasks",
                type: "numeric(10,3)",
                precision: 10,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "progress_percent",
                table: "tasks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "task_progress_credits",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    engineer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    points = table.Column<decimal>(type: "numeric(10,3)", precision: 10, scale: 3, nullable: false),
                    progress_after = table.Column<int>(type: "integer", nullable: false),
                    credited_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_progress_credits", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_task_progress_credits_engineer_id_credited_at",
                table: "task_progress_credits",
                columns: new[] { "engineer_id", "credited_at" });

            migrationBuilder.CreateIndex(
                name: "ix_task_progress_credits_task_id",
                table: "task_progress_credits",
                column: "task_id");

            // Tasks already finished have earned all their points: one credit each, dated at the task's latest Done transition and credited the way the
            // finished task is. Work still in flight starts at nothing, so no retroactive credit appears for it (a checklist already ticked off, or a task
            // already in QA, is credited the first time the task is next saved).

            // Row-level security, organization only (a task's own policy encodes access rules beyond organization, so this goes through the engineer, whose
            // policy is organization-only: the same shape as time_entries). A credit with no engineer, for a task nobody holds, is visible at this level and
            // kept inside its organization by the EF filter through its task.
            migrationBuilder.Sql("alter table public.task_progress_credits enable row level security;");
            migrationBuilder.Sql("alter table public.task_progress_credits force row level security;");
            migrationBuilder.Sql(@"
                create policy rls_task_progress_credits_organization on public.task_progress_credits for all
                    using (engineer_id is null or exists (select 1 from public.engineers e where e.id = task_progress_credits.engineer_id))
                    with check (engineer_id is null or exists (select 1 from public.engineers e where e.id = task_progress_credits.engineer_id));");

            migrationBuilder.Sql(BackfillSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "task_progress_credits");

            migrationBuilder.DropColumn(
                name: "earned_points",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "progress_percent",
                table: "tasks");
        }
    }
}
