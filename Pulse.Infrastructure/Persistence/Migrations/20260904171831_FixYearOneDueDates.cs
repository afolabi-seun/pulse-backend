using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FixYearOneDueDates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Data fixup: CreateTaskRequest.DueDate used to be a non-nullable DateOnly, so a task
            // created via the New Task form with no due date picked got 0001-01-01 stored instead
            // of NULL (fixed in application code separately). 1 Jan year 1 is not a date anyone
            // could have legitimately entered through the UI's date picker, so matching on the
            // exact sentinel value is unambiguous.
            migrationBuilder.Sql("UPDATE tasks SET due_date = NULL WHERE due_date = '0001-01-01';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversible — there's no way to recover which rows hit the bug vs. already had
            // no due date, and restoring 0001-01-01 wouldn't be a meaningful "undo" either way.
        }
    }
}
