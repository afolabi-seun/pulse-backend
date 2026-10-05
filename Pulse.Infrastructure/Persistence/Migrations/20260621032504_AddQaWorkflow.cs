using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddQaWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "parent_task_id",
                table: "tasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "qa_task_id",
                table: "tasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reactivation_reason",
                table: "tasks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "requires_qa",
                table: "tasks",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "parent_task_id",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "qa_task_id",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "reactivation_reason",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "requires_qa",
                table: "tasks");
        }
    }
}
