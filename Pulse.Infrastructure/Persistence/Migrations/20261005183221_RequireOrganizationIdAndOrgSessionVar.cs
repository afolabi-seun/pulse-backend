using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Multi-tenancy Phase 1a: tightens organization_id on teams/engineers/projects to NOT NULL (every row
    /// already has the default org via AddOrganizations' column default), and adds app.current_org_id() —
    /// the caller's organization, stamped per connection by RlsConnectionInterceptor from the JWT's org_id
    /// claim, mirroring app.current_user_id(). No policy calls it yet; Phase 1d adds the org RLS backstop.
    /// </summary>
    public partial class RequireOrganizationIdAndOrgSessionVar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                create or replace function app.current_org_id() returns uuid
                language sql stable as $$
                    select nullif(current_setting('app.current_org_id', true), '')::uuid
                $$;");

            migrationBuilder.AlterColumn<Guid>(
                name: "organization_id",
                table: "teams",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true,
                oldDefaultValue: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.AlterColumn<Guid>(
                name: "organization_id",
                table: "projects",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true,
                oldDefaultValue: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.AlterColumn<Guid>(
                name: "organization_id",
                table: "engineers",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true,
                oldDefaultValue: new Guid("00000000-0000-0000-0000-000000000001"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("drop function if exists app.current_org_id();");

            migrationBuilder.AlterColumn<Guid>(
                name: "organization_id",
                table: "teams",
                type: "uuid",
                nullable: true,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldDefaultValue: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.AlterColumn<Guid>(
                name: "organization_id",
                table: "projects",
                type: "uuid",
                nullable: true,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldDefaultValue: new Guid("00000000-0000-0000-0000-000000000001"));

            migrationBuilder.AlterColumn<Guid>(
                name: "organization_id",
                table: "engineers",
                type: "uuid",
                nullable: true,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldDefaultValue: new Guid("00000000-0000-0000-0000-000000000001"));
        }
    }
}
