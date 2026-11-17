using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace NexDesk.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Billing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "invoices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    plan_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    plan_name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    amount_cents = table.Column<int>(type: "integer", nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    period_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    period_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    billed_to_name = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    billed_to_email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    card_brand = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    card_last4 = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invoices", x => x.id);
                    table.ForeignKey(
                        name: "fk_invoices_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "plans",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    price_cents_monthly = table.Column<int>(type: "integer", nullable: false),
                    max_agents = table.Column<int>(type: "integer", nullable: true),
                    max_tickets_per_month = table.Column<int>(type: "integer", nullable: true),
                    max_storage_bytes = table.Column<long>(type: "bigint", nullable: false),
                    max_automation_rules = table.Column<int>(type: "integer", nullable: true),
                    api_access = table.Column<bool>(type: "boolean", nullable: false),
                    requests_per_minute = table.Column<int>(type: "integer", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plans", x => x.code);
                });

            migrationBuilder.CreateTable(
                name: "usage_counters",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period = table.Column<int>(type: "integer", nullable: false),
                    tickets_created = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_usage_counters", x => x.id);
                    table.ForeignKey(
                        name: "fk_usage_counters_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "subscriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    current_period_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    current_period_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    card_brand = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    card_last4 = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscriptions", x => x.id);
                    table.ForeignKey(
                        name: "fk_subscriptions_plans_plan_code",
                        column: x => x.plan_code,
                        principalTable: "plans",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_subscriptions_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "plans",
                columns: new[] { "code", "api_access", "max_agents", "max_automation_rules", "max_storage_bytes", "max_tickets_per_month", "name", "position", "price_cents_monthly", "requests_per_minute" },
                values: new object[,]
                {
                    { "business", true, null, null, 214748364800L, null, "Business", 2, 9900, 1200 },
                    { "free", false, 3, 3, 1073741824L, 100, "Free", 0, 0, 240 },
                    { "pro", false, 10, 10, 21474836480L, 2000, "Pro", 1, 2900, 600 }
                });

            migrationBuilder.CreateIndex(
                name: "ix_invoices_issued_at",
                table: "invoices",
                column: "issued_at");

            migrationBuilder.CreateIndex(
                name: "ix_invoices_tenant_id",
                table: "invoices",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_invoices_tenant_id_number",
                table: "invoices",
                columns: new[] { "tenant_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_current_period_end",
                table: "subscriptions",
                column: "current_period_end");

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_plan_code",
                table: "subscriptions",
                column: "plan_code");

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_tenant_id",
                table: "subscriptions",
                column: "tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_usage_counters_tenant_id",
                table: "usage_counters",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_usage_counters_tenant_id_period",
                table: "usage_counters",
                columns: new[] { "tenant_id", "period" },
                unique: true);

            // Workspaces that existed before plans did start on the free plan, with this month's tickets counted.
            migrationBuilder.Sql(
                """
                INSERT INTO subscriptions (id, tenant_id, plan_code, current_period_start, created_at)
                SELECT gen_random_uuid(), id, 'free', now(), now() FROM tenants;

                INSERT INTO usage_counters (id, tenant_id, period, tickets_created)
                SELECT gen_random_uuid(), tenant_id, (to_char(now() AT TIME ZONE 'UTC', 'YYYYMM'))::int, count(*)
                FROM tickets
                WHERE created_at >= date_trunc('month', now() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC'
                GROUP BY tenant_id;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "invoices");

            migrationBuilder.DropTable(
                name: "subscriptions");

            migrationBuilder.DropTable(
                name: "usage_counters");

            migrationBuilder.DropTable(
                name: "plans");
        }
    }
}
