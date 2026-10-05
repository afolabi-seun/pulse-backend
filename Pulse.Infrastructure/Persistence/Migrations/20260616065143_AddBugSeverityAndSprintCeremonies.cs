using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBugSeverityAndSprintCeremonies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "severity",
                table: "tasks",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "show_and_tell_date",
                table: "sprints",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "show_and_tell_notes",
                table: "sprints",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "severity",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "show_and_tell_date",
                table: "sprints");

            migrationBuilder.DropColumn(
                name: "show_and_tell_notes",
                table: "sprints");
        }
    }
}
