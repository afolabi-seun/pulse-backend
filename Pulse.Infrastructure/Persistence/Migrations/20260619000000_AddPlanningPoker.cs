using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlanningPoker : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "task_estimation_sessions",
                columns: table => new
                {
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_revealed = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_estimation_sessions", x => x.task_id);
                });

            migrationBuilder.CreateTable(
                name: "task_estimation_votes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    voter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    points = table.Column<int>(type: "integer", nullable: false),
                    submitted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_estimation_votes", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_task_estimation_votes_task_id",
                table: "task_estimation_votes",
                column: "task_id");

            migrationBuilder.CreateIndex(
                name: "ix_task_estimation_votes_task_voter",
                table: "task_estimation_votes",
                columns: new[] { "task_id", "voter_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "task_estimation_votes");
            migrationBuilder.DropTable(name: "task_estimation_sessions");
        }
    }
}
