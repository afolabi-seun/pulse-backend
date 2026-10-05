using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEscalationThresholds : Migration
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
                    { "EscalationT3Days",        "3"    },
                    { "EscalationT3ElapsedPct",  "0.60" },
                    { "EscalationT1Days",        "1"    },
                    { "EscalationT1ElapsedPct",  "0.85" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(table: "threshold_settings", keyColumn: "key", keyValue: "EscalationT3Days");
            migrationBuilder.DeleteData(table: "threshold_settings", keyColumn: "key", keyValue: "EscalationT3ElapsedPct");
            migrationBuilder.DeleteData(table: "threshold_settings", keyColumn: "key", keyValue: "EscalationT1Days");
            migrationBuilder.DeleteData(table: "threshold_settings", keyColumn: "key", keyValue: "EscalationT1ElapsedPct");
        }
    }
}
