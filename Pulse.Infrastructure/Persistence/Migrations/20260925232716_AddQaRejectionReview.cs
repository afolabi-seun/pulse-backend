using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddQaRejectionReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "pending_rejection_actor_id",
                table: "tasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pending_rejection_reason",
                table: "tasks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "pending_rejection_responded_by_engineer_id",
                table: "tasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pending_rejection_response",
                table: "tasks",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "pending_rejection_actor_id",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "pending_rejection_reason",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "pending_rejection_responded_by_engineer_id",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "pending_rejection_response",
                table: "tasks");
        }
    }
}
