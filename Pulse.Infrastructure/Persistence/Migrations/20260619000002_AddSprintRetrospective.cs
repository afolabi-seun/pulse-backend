using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSprintRetrospective : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sprint_retrospectives",
                columns: table => new
                {
                    id                = table.Column<Guid>(type: "uuid", nullable: false),
                    sprint_id         = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_id     = table.Column<Guid>(type: "uuid", nullable: false),
                    went_well         = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: false),
                    needs_improvement = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: false),
                    action_items      = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: false),
                    created_at        = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at        = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sprint_retrospectives", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sprint_retrospectives_sprint_id",
                table: "sprint_retrospectives",
                column: "sprint_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "sprint_retrospectives");
        }
    }
}
