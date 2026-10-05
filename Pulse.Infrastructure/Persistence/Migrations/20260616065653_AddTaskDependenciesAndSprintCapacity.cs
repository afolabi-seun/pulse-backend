using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskDependenciesAndSprintCapacity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "capacity_points",
                table: "sprints",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "task_dependencies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    blocking_task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dependent_task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_dependencies", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_task_dependencies_blocking_task_id",
                table: "task_dependencies",
                column: "blocking_task_id");

            migrationBuilder.CreateIndex(
                name: "ix_task_dependencies_dependent_task_id",
                table: "task_dependencies",
                column: "dependent_task_id");

            migrationBuilder.CreateIndex(
                name: "ix_task_dependencies_unique_pair",
                table: "task_dependencies",
                columns: new[] { "blocking_task_id", "dependent_task_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "task_dependencies");

            migrationBuilder.DropColumn(
                name: "capacity_points",
                table: "sprints");
        }
    }
}
