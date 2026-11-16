using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace NexDesk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TicketProductivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "custom_fields",
                table: "tickets",
                type: "jsonb",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "merged_into_id",
                table: "tickets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "tickets",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<string>(
                name: "field",
                table: "ticket_events",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "search_vector",
                table: "tickets",
                type: "tsvector",
                nullable: true,
                computedColumnSql: "to_tsvector('simple', subject)",
                stored: true);

            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "search_vector",
                table: "ticket_messages",
                type: "tsvector",
                nullable: true,
                computedColumnSql: "to_tsvector('simple', body)",
                stored: true);

            migrationBuilder.CreateTable(
                name: "custom_fields",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    label = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    options = table.Column<List<string>>(type: "text[]", nullable: false),
                    is_required = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_custom_fields", x => x.id);
                    table.ForeignKey(
                        name: "fk_custom_fields_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "saved_views",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    is_shared = table.Column<bool>(type: "boolean", nullable: false),
                    filter = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_saved_views", x => x.id);
                    table.ForeignKey(
                        name: "fk_saved_views_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_saved_views_users_owner_id",
                        column: x => x.owner_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_merged_into_id",
                table: "tickets",
                column: "merged_into_id");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_search_vector",
                table: "tickets",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_messages_search_vector",
                table: "ticket_messages",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_custom_fields_tenant_id",
                table: "custom_fields",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_custom_fields_tenant_id_key",
                table: "custom_fields",
                columns: new[] { "tenant_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_saved_views_owner_id",
                table: "saved_views",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "ix_saved_views_tenant_id",
                table: "saved_views",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_saved_views_tenant_id_owner_id",
                table: "saved_views",
                columns: new[] { "tenant_id", "owner_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_tickets_tickets_merged_into_id",
                table: "tickets",
                column: "merged_into_id",
                principalTable: "tickets",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_tickets_tickets_merged_into_id",
                table: "tickets");

            migrationBuilder.DropTable(
                name: "custom_fields");

            migrationBuilder.DropTable(
                name: "saved_views");

            migrationBuilder.DropIndex(
                name: "ix_tickets_merged_into_id",
                table: "tickets");

            migrationBuilder.DropIndex(
                name: "ix_tickets_search_vector",
                table: "tickets");

            migrationBuilder.DropIndex(
                name: "ix_ticket_messages_search_vector",
                table: "ticket_messages");

            migrationBuilder.DropColumn(
                name: "search_vector",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "search_vector",
                table: "ticket_messages");

            migrationBuilder.DropColumn(
                name: "custom_fields",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "merged_into_id",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "xmin",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "field",
                table: "ticket_events");
        }
    }
}
