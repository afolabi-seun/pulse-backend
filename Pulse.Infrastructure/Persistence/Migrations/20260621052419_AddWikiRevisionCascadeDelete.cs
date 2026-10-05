using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWikiRevisionCascadeDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddForeignKey(
                name: "FK_wiki_page_revisions_wiki_pages_wiki_page_id",
                table: "wiki_page_revisions",
                column: "wiki_page_id",
                principalTable: "wiki_pages",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_wiki_page_revisions_wiki_pages_wiki_page_id",
                table: "wiki_page_revisions");
        }
    }
}
