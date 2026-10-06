using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Multi-tenancy Phase 2c: google_chat_spaces.organization_id becomes nullable with no default, so a space
    /// Pulse is newly added to starts unlinked — visible to no organization (org RLS and the EF filter both
    /// compare against the caller's org, which null never equals) — until a head links it with a one-time
    /// code from google_chat_link_codes. Spaces registered before this keep the default organization.
    /// </summary>
    public partial class LinkGoogleChatSpacesToOrganizations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "organization_id",
                table: "google_chat_spaces",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldDefaultValue: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.CreateTable(
                name: "google_chat_link_codes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by_engineer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_google_chat_link_codes", x => x.id);
                    table.ForeignKey(
                        name: "fk_google_chat_link_codes_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_google_chat_link_codes_organization_id",
                table: "google_chat_link_codes",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ux_google_chat_link_codes_code_hash",
                table: "google_chat_link_codes",
                column: "code_hash",
                unique: true);

            migrationBuilder.Sql("alter table public.google_chat_link_codes enable row level security;");
            migrationBuilder.Sql("alter table public.google_chat_link_codes force row level security;");
            migrationBuilder.Sql(@"
                create policy rls_google_chat_link_codes_organization on public.google_chat_link_codes for all
                    using (app.org_visible(organization_id)) with check (app.org_visible(organization_id));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Unlinked spaces have no organization; give them the default one so the column can be required again.
            migrationBuilder.Sql("update public.google_chat_spaces set organization_id = '00000000-0000-0000-0000-000000000001' where organization_id is null;");

            migrationBuilder.DropTable(
                name: "google_chat_link_codes");

            migrationBuilder.AlterColumn<Guid>(
                name: "organization_id",
                table: "google_chat_spaces",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
