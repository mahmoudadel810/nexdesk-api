using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexDesk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantSuspension : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "suspended_at",
                table: "tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "suspension_reason",
                table: "tenants",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_tenants_created_at",
                table: "tenants",
                column: "created_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_tenants_created_at",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "suspended_at",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "suspension_reason",
                table: "tenants");
        }
    }
}
