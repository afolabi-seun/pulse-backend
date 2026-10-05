using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectPauseHold : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "paused_at",
                table: "tasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "paused_by_project",
                table: "tasks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "status_before_pause",
                table: "tasks",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "projects",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Active");

            // Backfill from is_active before dropping it — every existing project was either
            // active or archived (Paused didn't exist yet), so this covers all rows exactly.
            migrationBuilder.Sql(
                "UPDATE projects SET status = CASE WHEN is_active THEN 'Active' ELSE 'Archived' END;");

            migrationBuilder.DropIndex(
                name: "ix_projects_is_active",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "is_active",
                table: "projects");

            migrationBuilder.CreateIndex(
                name: "ix_projects_status",
                table: "projects",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_active",
                table: "projects",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql(
                "UPDATE projects SET is_active = (status = 'Active');");

            migrationBuilder.DropIndex(
                name: "ix_projects_status",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "paused_at",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "paused_by_project",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "status_before_pause",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "status",
                table: "projects");

            migrationBuilder.CreateIndex(
                name: "ix_projects_is_active",
                table: "projects",
                column: "is_active");
        }
    }
}
