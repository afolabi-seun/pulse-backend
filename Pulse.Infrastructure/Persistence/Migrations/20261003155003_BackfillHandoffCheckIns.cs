using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Data fix: hand-offs and Send-to-QA that happened before AutoCheckIn recorded them left nothing in
    /// the daily check-in. See <see cref="HandoffCheckInBackfill"/> for the rule, how to preview its
    /// effect on real data before deploying, and why it's safe to re-run.
    /// </summary>
    public partial class BackfillHandoffCheckIns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(HandoffCheckInBackfill.Sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversible — a backfilled entry is indistinguishable from one written live.
        }
    }
}
