using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Backfill for the new Backlog status (see PulseTask.cs / TaskStatus.cs): rows created before
    /// that change default to Active regardless of assignee, so unassigned tasks already in the
    /// database were still escalating to PMs/department heads via EscalationScanner. Moves every
    /// currently-unassigned Active or Blocked task to Backlog — Blocked is included alongside Active
    /// because both count as "active workload" (TaskStatusExtensions.CountsAsActiveWorkload) and both
    /// feed the escalation scanner; Done/InQa/Paused are already excluded there and left untouched.
    ///
    /// No TaskHistory "status" row is written for the affected tasks — this is a one-time system
    /// correction with no real actor to attribute it to, not a normal domain-driven transition.
    /// Down is intentionally a no-op: the affected tasks' prior split between Active and Blocked
    /// isn't recoverable from the Backlog value alone, so a "restore to Active" rollback would be
    /// lossy and wrong for anything that was actually Blocked.
    /// </summary>
    public partial class BacklogUnassignedActiveTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                UPDATE tasks
                SET status = 'Backlog'
                WHERE assignee_id IS NULL
                  AND status IN ('Active', 'Blocked');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversible — see class summary.
        }
    }
}
