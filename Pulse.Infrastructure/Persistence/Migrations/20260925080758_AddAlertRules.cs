using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAlertRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "alert_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_engineer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    metric = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    scope_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    comparator = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    threshold = table.Column<double>(type: "double precision", nullable: false),
                    deliver_in_app = table.Column<bool>(type: "boolean", nullable: false),
                    deliver_email = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    last_triggered_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_rules", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_alert_rules_active_metric_scope",
                table: "alert_rules",
                columns: new[] { "is_active", "metric", "scope_type", "scope_id" });

            migrationBuilder.CreateIndex(
                name: "ix_alert_rules_owner_engineer_id",
                table: "alert_rules",
                column: "owner_engineer_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alert_rules");
        }
    }
}
