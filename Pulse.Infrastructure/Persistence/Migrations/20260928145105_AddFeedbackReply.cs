using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFeedbackReply : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "replied_at",
                table: "feedback",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "replied_by",
                table: "feedback",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reply_text",
                table: "feedback",
                type: "character varying(5000)",
                maxLength: 5000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "replied_at",
                table: "feedback");

            migrationBuilder.DropColumn(
                name: "replied_by",
                table: "feedback");

            migrationBuilder.DropColumn(
                name: "reply_text",
                table: "feedback");
        }
    }
}
