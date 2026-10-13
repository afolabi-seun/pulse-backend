using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWikiPageCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "category",
                table: "wiki_pages",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "general");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "category",
                table: "wiki_pages");
        }
    }
}
