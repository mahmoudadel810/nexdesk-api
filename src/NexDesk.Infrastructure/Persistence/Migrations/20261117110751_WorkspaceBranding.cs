using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexDesk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkspaceBranding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "brand_color",
                table: "tenants",
                type: "character varying(7)",
                maxLength: 7,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "logo_content_type",
                table: "tenants",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "logo_file_key",
                table: "tenants",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "logo_size_bytes",
                table: "tenants",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "logo_updated_at",
                table: "tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "portal_welcome_text",
                table: "tenants",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "brand_color",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "logo_content_type",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "logo_file_key",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "logo_size_bytes",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "logo_updated_at",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "portal_welcome_text",
                table: "tenants");
        }
    }
}
