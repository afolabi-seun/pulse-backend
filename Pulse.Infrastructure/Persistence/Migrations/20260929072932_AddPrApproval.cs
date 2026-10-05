using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPrApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "pending_pr_approval_delegated_to_engineer_id",
                table: "tasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "pending_pr_approval_requested_at",
                table: "tasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "pending_pr_approval_requested_by_engineer_id",
                table: "tasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "pr_approved_at",
                table: "tasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pr_link",
                table: "tasks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "requires_pr_approval",
                table: "tasks",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "pending_pr_approval_delegated_to_engineer_id",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "pending_pr_approval_requested_at",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "pending_pr_approval_requested_by_engineer_id",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "pr_approved_at",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "pr_link",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "requires_pr_approval",
                table: "tasks");
        }
    }
}
