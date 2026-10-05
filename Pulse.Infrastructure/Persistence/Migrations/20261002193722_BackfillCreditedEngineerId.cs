using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Data fix: Done-transition history rows written before CreditedEngineerId existed have no
    /// credit, so reports attributed past backend->frontend handoffs to the frontend engineer and
    /// past loans to the borrower. See <see cref="CreditedEngineerBackfill"/> for the rule, how to
    /// preview its effect on real data before deploying, and why it's safe to re-run.
    /// </summary>
    public partial class BackfillCreditedEngineerId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(CreditedEngineerBackfill.Sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversible — once filled in, a backfilled credit is indistinguishable from one
            // written live by MarkDone/AcceptQa.
        }
    }
}
