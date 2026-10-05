using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCheckInProjectId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_check_ins_engineer_id_date",
                table: "check_ins");

            migrationBuilder.AddColumn<Guid>(
                name: "project_id",
                table: "check_ins",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_check_ins_engineer_id_date_project_id",
                table: "check_ins",
                columns: new[] { "engineer_id", "date", "project_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_check_ins_engineer_id_date_project_id",
                table: "check_ins");

            migrationBuilder.DropColumn(
                name: "project_id",
                table: "check_ins");

            migrationBuilder.CreateIndex(
                name: "ix_check_ins_engineer_id_date",
                table: "check_ins",
                columns: new[] { "engineer_id", "date" },
                unique: true);
        }
    }
}
