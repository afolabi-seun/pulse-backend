using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Multi-tenancy Phase 0 (docs/design/multi-tenancy-and-billing.md): adds the organizations table,
    /// seeds the default organization (Organization.DefaultId), and adds a nullable organization_id,
    /// defaulting to it, to teams, engineers and projects. Purely additive — nothing reads the column
    /// yet; Phase 1 tightens it to NOT NULL and makes RLS and ProjectAccessPolicy enforce it.
    /// </summary>
    public partial class AddOrganizations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. The organizations table, seeded with the one default org every existing row belongs to.
            migrationBuilder.CreateTable(
                name: "organizations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    slug = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    billing_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organizations", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_organizations_slug",
                table: "organizations",
                column: "slug",
                unique: true);

            migrationBuilder.Sql(@"
                INSERT INTO organizations (id, name, slug, billing_email, is_active, created_at)
                VALUES ('00000000-0000-0000-0000-000000000001', 'Default organization', 'default', NULL, true, now());");

            // 2. organization_id on the three tenancy-root tables. There is deliberately no UPDATE
            // backfill: adding a column with a constant DEFAULT makes Postgres (11+) fill every
            // existing row with that value as a metadata-only change. That also sidesteps the trap
            // AddProjectCodeAndTaskNumber documents — projects is FORCE ROW LEVEL SECURITY, so an
            // UPDATE run by this migration's (non-superuser, table-owner) role would silently match
            // zero rows. DDL isn't subject to RLS, so no NO FORCE toggle is needed here.
            //
            // The default also covers inserts from the still-running old app version during a rolling
            // deploy, and every insert after it until Phase 2 sets the caller's org explicitly.
            foreach (var table in new[] { "teams", "engineers", "projects" })
            {
                migrationBuilder.AddColumn<Guid>(
                    name: "organization_id",
                    table: table,
                    type: "uuid",
                    nullable: true,
                    defaultValue: new Guid("00000000-0000-0000-0000-000000000001"));

                migrationBuilder.CreateIndex(
                    name: $"ix_{table}_organization_id",
                    table: table,
                    column: "organization_id");

                migrationBuilder.AddForeignKey(
                    name: $"fk_{table}_organizations_organization_id",
                    table: table,
                    column: "organization_id",
                    principalTable: "organizations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_engineers_organizations_organization_id",
                table: "engineers");

            migrationBuilder.DropForeignKey(
                name: "fk_projects_organizations_organization_id",
                table: "projects");

            migrationBuilder.DropForeignKey(
                name: "fk_teams_organizations_organization_id",
                table: "teams");

            migrationBuilder.DropTable(
                name: "organizations");

            migrationBuilder.DropIndex(
                name: "ix_teams_organization_id",
                table: "teams");

            migrationBuilder.DropIndex(
                name: "ix_projects_organization_id",
                table: "projects");

            migrationBuilder.DropIndex(
                name: "ix_engineers_organization_id",
                table: "engineers");

            migrationBuilder.DropColumn(
                name: "organization_id",
                table: "teams");

            migrationBuilder.DropColumn(
                name: "organization_id",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "organization_id",
                table: "engineers");
        }
    }
}
