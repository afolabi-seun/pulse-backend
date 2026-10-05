using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDepartmentOverworkThresholds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "department_overwork_thresholds",
                columns: table => new
                {
                    department = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    load_vs_baseline_ratio = table.Column<double>(type: "double precision", nullable: true),
                    max_concurrent_tasks = table.Column<int>(type: "integer", nullable: true),
                    stale_cycle_multiplier = table.Column<double>(type: "double precision", nullable: true),
                    signals_required_to_flag = table.Column<int>(type: "integer", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_department_overwork_thresholds", x => x.department);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "department_overwork_thresholds");
        }
    }
}
