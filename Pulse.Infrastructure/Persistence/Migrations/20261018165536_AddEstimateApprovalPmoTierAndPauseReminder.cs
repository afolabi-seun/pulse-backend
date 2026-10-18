using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEstimateApprovalPmoTierAndPauseReminder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "pause_reminded_at",
                table: "tasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "escalated_to_head_at",
                table: "task_estimation_sessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "escalated_to_pmo",
                table: "task_estimation_sessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "pause_reminded_at",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "escalated_to_head_at",
                table: "task_estimation_sessions");

            migrationBuilder.DropColumn(
                name: "escalated_to_pmo",
                table: "task_estimation_sessions");
        }
    }
}
