using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddThresholdSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "threshold_settings",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    value = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_threshold_settings", x => x.key);
                });

            migrationBuilder.InsertData(
                table: "threshold_settings",
                columns: new[] { "key", "value" },
                values: new object[,]
                {
                    { "LoadVsBaselineRatio", "1.3" },
                    { "MaxConcurrentTasks", "3" },
                    { "SignalsRequiredToFlag", "2" },
                    { "StaleCycleMultiplier", "1.5" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "threshold_settings");
        }
    }
}
