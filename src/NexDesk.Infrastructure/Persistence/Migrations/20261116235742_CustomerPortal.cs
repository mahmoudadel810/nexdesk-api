using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexDesk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CustomerPortal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "email_verified_at",
                table: "customers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "failed_login_count",
                table: "customers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_login_at",
                table: "customers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "locked_until",
                table: "customers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "password_hash",
                table: "customers",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "security_version",
                table: "customers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "csat_responses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rating = table.Column<int>(type: "integer", nullable: false),
                    comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_csat_responses", x => x.id);
                    table.ForeignKey(
                        name: "fk_csat_responses_customers_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_csat_responses_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_csat_responses_customer_id",
                table: "csat_responses",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_csat_responses_tenant_id",
                table: "csat_responses",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_csat_responses_tenant_id_created_at",
                table: "csat_responses",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_csat_responses_ticket_id",
                table: "csat_responses",
                column: "ticket_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "csat_responses");

            migrationBuilder.DropColumn(
                name: "email_verified_at",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "failed_login_count",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "last_login_at",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "locked_until",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "password_hash",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "security_version",
                table: "customers");
        }
    }
}
