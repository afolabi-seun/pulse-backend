using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWeeklyReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "weekly_reports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    team_id = table.Column<Guid>(type: "uuid", nullable: false),
                    week_of = table.Column<DateOnly>(type: "date", nullable: false),
                    author_id = table.Column<Guid>(type: "uuid", nullable: false),
                    executive_summary = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    key_accomplishments = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    planned_next_week = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    resourcing_notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    submitted_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    submitted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_weekly_reports", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_weekly_reports_team_id_week_of",
                table: "weekly_reports",
                columns: new[] { "team_id", "week_of" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "weekly_reports");
        }
    }
}
