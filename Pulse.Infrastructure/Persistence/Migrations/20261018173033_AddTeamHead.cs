using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamHead : Migration
    {
        /// <summary>The one-off fill-in, public so a test can run it against real rows.</summary>
        public const string BackfillSql = @"
                update teams t set head_id = c.head_id
                from (
                    select t1.id as team_id, (array_agg(e.id))[1] as head_id, count(distinct e.id) as heads
                    from teams t1
                    join teams t2 on t2.department = t1.department and t2.organization_id = t1.organization_id
                    join engineers e on e.team_id = t2.id and e.organization_id = t1.organization_id and e.is_active
                        and e.role in ('head_of_rd', 'head_of_product', 'head_of_design', 'head_of_functional', 'head_of_core_banking', 'head_of_infra_devops')
                    where t1.department is not null
                    group by t1.id
                ) c
                where t.id = c.team_id and c.heads = 1 and t.head_id is null;";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "head_id",
                table: "teams",
                type: "uuid",
                nullable: true);

            // Fill in a team's head only where it is unambiguous: exactly one active department head sits in the team's department, within the same
            // organization. Where two heads share a department label (the very case this column exists for) the team is left empty to be set by hand.
            migrationBuilder.Sql(BackfillSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "head_id",
                table: "teams");
        }
    }
}
