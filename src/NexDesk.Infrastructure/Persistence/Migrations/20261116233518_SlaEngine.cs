using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexDesk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SlaEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "first_response_breached_at",
                table: "tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "first_response_due_at",
                table: "tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "first_response_warned_at",
                table: "tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_escalated",
                table: "tickets",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "resolution_breached_at",
                table: "tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "resolution_due_at",
                table: "tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "resolution_remaining_minutes",
                table: "tickets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "resolution_warned_at",
                table: "tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "sla_first_response_minutes",
                table: "tickets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "sla_resolution_minutes",
                table: "tickets",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "holidays",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_holidays", x => x.id);
                    table.ForeignKey(
                        name: "fk_holidays_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sla_breaches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    metric = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    breached_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sla_breaches", x => x.id);
                    table.ForeignKey(
                        name: "fk_sla_breaches_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sla_policies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    priority = table.Column<int>(type: "integer", maxLength: 32, nullable: false),
                    first_response_minutes = table.Column<int>(type: "integer", nullable: false),
                    resolution_minutes = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sla_policies", x => x.id);
                    table.ForeignKey(
                        name: "fk_sla_policies_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_tenant_id_first_response_due_at",
                table: "tickets",
                columns: new[] { "tenant_id", "first_response_due_at" });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_tenant_id_resolution_due_at",
                table: "tickets",
                columns: new[] { "tenant_id", "resolution_due_at" });

            migrationBuilder.CreateIndex(
                name: "ix_holidays_tenant_id",
                table: "holidays",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_holidays_tenant_id_date",
                table: "holidays",
                columns: new[] { "tenant_id", "date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sla_breaches_tenant_id",
                table: "sla_breaches",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_sla_breaches_tenant_id_breached_at",
                table: "sla_breaches",
                columns: new[] { "tenant_id", "breached_at" });

            migrationBuilder.CreateIndex(
                name: "ix_sla_breaches_ticket_id_metric",
                table: "sla_breaches",
                columns: new[] { "ticket_id", "metric" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sla_policies_tenant_id",
                table: "sla_policies",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_sla_policies_tenant_id_priority",
                table: "sla_policies",
                columns: new[] { "tenant_id", "priority" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "holidays");

            migrationBuilder.DropTable(
                name: "sla_breaches");

            migrationBuilder.DropTable(
                name: "sla_policies");

            migrationBuilder.DropIndex(
                name: "ix_tickets_tenant_id_first_response_due_at",
                table: "tickets");

            migrationBuilder.DropIndex(
                name: "ix_tickets_tenant_id_resolution_due_at",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "first_response_breached_at",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "first_response_due_at",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "first_response_warned_at",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "is_escalated",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "resolution_breached_at",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "resolution_due_at",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "resolution_remaining_minutes",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "resolution_warned_at",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "sla_first_response_minutes",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "sla_resolution_minutes",
                table: "tickets");
        }
    }
}
