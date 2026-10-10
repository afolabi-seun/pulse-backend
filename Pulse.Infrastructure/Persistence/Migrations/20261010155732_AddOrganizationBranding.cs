using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Organization branding: organizations.brand_color and logo_updated_at, and organization_logos (the logo
    /// image, in its own table so it's never loaded with ordinary organization lookups). Org RLS like every table.
    /// </summary>
    public partial class AddOrganizationBranding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "brand_color",
                table: "organizations",
                type: "character varying(7)",
                maxLength: 7,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "logo_updated_at",
                table: "organizations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "organization_logos",
                columns: table => new
                {
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    data = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_logos", x => x.organization_id);
                    table.ForeignKey(
                        name: "fk_organization_logos_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("alter table public.organization_logos enable row level security;");
            migrationBuilder.Sql("alter table public.organization_logos force row level security;");
            migrationBuilder.Sql(@"
                create policy rls_organization_logos_organization on public.organization_logos for all
                    using (app.org_visible(organization_id)) with check (app.org_visible(organization_id));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "organization_logos");

            migrationBuilder.DropColumn(
                name: "brand_color",
                table: "organizations");

            migrationBuilder.DropColumn(
                name: "logo_updated_at",
                table: "organizations");
        }
    }
}
