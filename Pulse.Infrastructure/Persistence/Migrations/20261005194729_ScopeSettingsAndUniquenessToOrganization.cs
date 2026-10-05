using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Multi-tenancy Phase 1b (schema completion):
    ///   - organization_id on the org-wide tables with no other path to an org: threshold_settings and
    ///     department_overwork_thresholds (now keyed (organization_id, key/department)), google_chat_spaces
    ///     and failed_emails. Existing rows get the default org from the column default, as in AddOrganizations.
    ///   - Team name and project code become unique per organization instead of app-wide.
    /// engineers.email deliberately stays globally unique: one account per email, one org per account.
    /// Runs in one transaction, so there's no window where the old unique indexes are gone and the new
    /// ones don't exist yet.
    /// </summary>
    public partial class ScopeSettingsAndUniquenessToOrganization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_threshold_settings",
                table: "threshold_settings");

            migrationBuilder.DropIndex(
                name: "ix_teams_name",
                table: "teams");

            migrationBuilder.DropIndex(
                name: "ix_teams_organization_id",
                table: "teams");

            migrationBuilder.DropIndex(
                name: "ix_projects_organization_id",
                table: "projects");

            migrationBuilder.DropIndex(
                name: "ux_projects_code",
                table: "projects");

            migrationBuilder.DropPrimaryKey(
                name: "PK_department_overwork_thresholds",
                table: "department_overwork_thresholds");

            migrationBuilder.AddColumn<Guid>(
                name: "organization_id",
                table: "threshold_settings",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.AddColumn<Guid>(
                name: "organization_id",
                table: "google_chat_spaces",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.AddColumn<Guid>(
                name: "organization_id",
                table: "failed_emails",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.AddColumn<Guid>(
                name: "organization_id",
                table: "department_overwork_thresholds",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.AddPrimaryKey(
                name: "PK_threshold_settings",
                table: "threshold_settings",
                columns: new[] { "organization_id", "key" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_department_overwork_thresholds",
                table: "department_overwork_thresholds",
                columns: new[] { "organization_id", "department" });

            migrationBuilder.CreateIndex(
                name: "ux_teams_organization_id_name",
                table: "teams",
                columns: new[] { "organization_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_projects_organization_id_code",
                table: "projects",
                columns: new[] { "organization_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_google_chat_spaces_organization_id",
                table: "google_chat_spaces",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_failed_emails_organization_id",
                table: "failed_emails",
                column: "organization_id");

            migrationBuilder.AddForeignKey(
                name: "fk_department_overwork_thresholds_organizations_organization_id",
                table: "department_overwork_thresholds",
                column: "organization_id",
                principalTable: "organizations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_failed_emails_organizations_organization_id",
                table: "failed_emails",
                column: "organization_id",
                principalTable: "organizations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_google_chat_spaces_organizations_organization_id",
                table: "google_chat_spaces",
                column: "organization_id",
                principalTable: "organizations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_threshold_settings_organizations_organization_id",
                table: "threshold_settings",
                column: "organization_id",
                principalTable: "organizations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_department_overwork_thresholds_organizations_organization_id",
                table: "department_overwork_thresholds");

            migrationBuilder.DropForeignKey(
                name: "fk_failed_emails_organizations_organization_id",
                table: "failed_emails");

            migrationBuilder.DropForeignKey(
                name: "fk_google_chat_spaces_organizations_organization_id",
                table: "google_chat_spaces");

            migrationBuilder.DropForeignKey(
                name: "fk_threshold_settings_organizations_organization_id",
                table: "threshold_settings");

            migrationBuilder.DropPrimaryKey(
                name: "PK_threshold_settings",
                table: "threshold_settings");

            migrationBuilder.DropIndex(
                name: "ux_teams_organization_id_name",
                table: "teams");

            migrationBuilder.DropIndex(
                name: "ux_projects_organization_id_code",
                table: "projects");

            migrationBuilder.DropIndex(
                name: "ix_google_chat_spaces_organization_id",
                table: "google_chat_spaces");

            migrationBuilder.DropIndex(
                name: "ix_failed_emails_organization_id",
                table: "failed_emails");

            migrationBuilder.DropPrimaryKey(
                name: "PK_department_overwork_thresholds",
                table: "department_overwork_thresholds");

            migrationBuilder.DropColumn(
                name: "organization_id",
                table: "threshold_settings");

            migrationBuilder.DropColumn(
                name: "organization_id",
                table: "google_chat_spaces");

            migrationBuilder.DropColumn(
                name: "organization_id",
                table: "failed_emails");

            migrationBuilder.DropColumn(
                name: "organization_id",
                table: "department_overwork_thresholds");

            migrationBuilder.AddPrimaryKey(
                name: "PK_threshold_settings",
                table: "threshold_settings",
                column: "key");

            migrationBuilder.AddPrimaryKey(
                name: "PK_department_overwork_thresholds",
                table: "department_overwork_thresholds",
                column: "department");

            migrationBuilder.CreateIndex(
                name: "ix_teams_name",
                table: "teams",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_teams_organization_id",
                table: "teams",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_projects_organization_id",
                table: "projects",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ux_projects_code",
                table: "projects",
                column: "code",
                unique: true);
        }
    }
}
