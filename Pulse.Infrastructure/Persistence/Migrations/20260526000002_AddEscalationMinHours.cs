using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEscalationMinHours : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "threshold_settings",
                columns: new[] { "key", "value" },
                columnTypes: new[] { "character varying(100)", "character varying(100)" },
                values: new object[,]
                {
                    { "EscalationT3MinHours", "2" },
                    { "EscalationT1MinHours", "1" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(table: "threshold_settings", keyColumn: "key", keyValue: "EscalationT3MinHours");
            migrationBuilder.DeleteData(table: "threshold_settings", keyColumn: "key", keyValue: "EscalationT1MinHours");
        }
    }
}
