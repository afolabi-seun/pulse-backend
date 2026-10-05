using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSprintProjectId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "project_id",
                table: "sprints",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_sprints_project_id",
                table: "sprints",
                column: "project_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_sprints_project_id",
                table: "sprints");

            migrationBuilder.DropColumn(
                name: "project_id",
                table: "sprints");
        }
    }
}
