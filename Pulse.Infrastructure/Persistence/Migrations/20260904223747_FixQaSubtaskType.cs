using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FixQaSubtaskType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Data fixup: SendToQaCommand hardcoded TaskType.Feature on every auto-created "[QA]
            // ..." sub-task instead of TaskType.Review (fixed in application code separately).
            // Scoped to rows that are unambiguously auto-created QA sub-tasks — has a parent and
            // the "[QA] " title prefix SendToQaCommand always uses — so nothing else is touched.
            migrationBuilder.Sql(
                "UPDATE tasks SET type = 'Review' " +
                "WHERE parent_task_id IS NOT NULL AND type = 'Feature' AND title LIKE '[QA] %';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversible — there's no way to tell which rows were fixed here vs. genuinely
            // created as Review before this migration ran.
        }
    }
}
