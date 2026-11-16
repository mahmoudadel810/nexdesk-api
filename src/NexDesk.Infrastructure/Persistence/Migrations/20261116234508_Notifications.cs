using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexDesk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Notifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notification_preferences",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    in_app = table.Column<bool>(type: "boolean", nullable: false),
                    email = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_preferences", x => new { x.user_id, x.type });
                    table.ForeignKey(
                        name: "fk_notification_preferences_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_number = table.Column<int>(type: "integer", nullable: false),
                    ticket_subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    actor_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    preview = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications", x => x.id);
                    table.ForeignKey(
                        name: "fk_notifications_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_notifications_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_notification_preferences_tenant_id",
                table: "notification_preferences",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_tenant_id",
                table: "notifications",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_ticket_id",
                table: "notifications",
                column: "ticket_id");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_user_id_read_at_created_at",
                table: "notifications",
                columns: new[] { "user_id", "read_at", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notification_preferences");

            migrationBuilder.DropTable(
                name: "notifications");
        }
    }
}
