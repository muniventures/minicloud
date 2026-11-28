using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Selfrun.Common.Migrations
{
    /// <inheritdoc />
    public partial class EnvironmentPowerPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "environment_power_operations",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    environment_id = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    trigger = table.Column<string>(type: "text", nullable: false),
                    actor_json = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    step = table.Column<string>(type: "text", nullable: true),
                    ownership_token = table.Column<string>(type: "text", nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    error_code = table.Column<string>(type: "text", nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deployment_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_environment_power_operations", x => x.id);
                    table.CheckConstraint("ck_environment_power_operation_kind", "kind IN ('sleep', 'wake')");
                    table.CheckConstraint("ck_environment_power_operation_status", "status IN ('queued', 'running', 'cancelling') OR status IN ('completed', 'failed', 'cancelled')");
                    table.ForeignKey(
                        name: "FK_environment_power_operations_apps_organization_id_environme~",
                        columns: x => new { x.organization_id, x.environment_id },
                        principalTable: "apps",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "environment_retained_addresses",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    environment_id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false),
                    provider_account_id = table.Column<string>(type: "text", nullable: false),
                    provider_address_id = table.Column<string>(type: "text", nullable: false),
                    ipv4 = table.Column<string>(type: "text", nullable: false),
                    region = table.Column<string>(type: "text", nullable: false),
                    datacenter = table.Column<string>(type: "text", nullable: true),
                    previous_ipv4 = table.Column<string>(type: "text", nullable: true),
                    state = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_environment_retained_addresses", x => x.id);
                    table.CheckConstraint("ck_environment_retained_address_state", "state IN ('assigned', 'unassigned', 'releasing', 'released', 'failed')");
                    table.ForeignKey(
                        name: "FK_environment_retained_addresses_provider_accounts_organizati~",
                        columns: x => new { x.organization_id, x.provider_account_id },
                        principalTable: "provider_accounts",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "environment_sleep_settings",
                columns: table => new
                {
                    environment_id = table.Column<string>(type: "text", nullable: false),
                    sleep_after_minutes = table.Column<int>(type: "integer", nullable: true),
                    http_traffic_is_activity = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    address_mode = table.Column<string>(type: "text", nullable: true),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    power_state = table.Column<string>(type: "text", nullable: false, defaultValue: "awake"),
                    state_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_activity_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_activity_kind = table.Column<string>(type: "text", nullable: true),
                    last_http_request_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    keep_awake_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error_code = table.Column<string>(type: "text", nullable: true),
                    last_error_message = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_environment_sleep_settings", x => x.environment_id);
                    table.CheckConstraint("ck_environment_sleep_address_mode", "address_mode IN ('dynamic', 'retained')");
                    table.CheckConstraint("ck_environment_sleep_power_state", "power_state IN ('awake', 'sleeping', 'asleep', 'waking', 'sleep_failed', 'wake_failed')");
                    table.CheckConstraint("ck_environment_sleep_timeout", "sleep_after_minutes BETWEEN 30 AND 43200");
                    table.ForeignKey(
                        name: "FK_environment_sleep_settings_apps_environment_id",
                        column: x => x.environment_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "environment_snapshots",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    environment_id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false),
                    provider_account_id = table.Column<string>(type: "text", nullable: false),
                    provider_image_id = table.Column<string>(type: "text", nullable: true),
                    region = table.Column<string>(type: "text", nullable: false),
                    datacenter = table.Column<string>(type: "text", nullable: true),
                    sku = table.Column<string>(type: "text", nullable: false),
                    server_name = table.Column<string>(type: "text", nullable: false),
                    address_mode = table.Column<string>(type: "text", nullable: false),
                    size_gb = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    placement_json = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_environment_snapshots", x => x.id);
                    table.CheckConstraint("ck_environment_snapshot_address_mode", "address_mode IN ('dynamic', 'retained')");
                    table.CheckConstraint("ck_environment_snapshot_current_image", "status <> 'current' OR provider_image_id IS NOT NULL");
                    table.CheckConstraint("ck_environment_snapshot_status", "status IN ('creating', 'pending', 'current', 'superseded', 'deleting', 'deleted', 'failed')");
                    table.ForeignKey(
                        name: "FK_environment_snapshots_provider_accounts_organization_id_pro~",
                        columns: x => new { x.organization_id, x.provider_account_id },
                        principalTable: "provider_accounts",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_environment_power_operations_deployment_id",
                table: "environment_power_operations",
                column: "deployment_id");

            migrationBuilder.CreateIndex(
                name: "IX_environment_power_operations_organization_id_environment_id",
                table: "environment_power_operations",
                columns: new[] { "organization_id", "environment_id" });

            migrationBuilder.CreateIndex(
                name: "IX_environment_power_operations_status_lease_expires_at_update~",
                table: "environment_power_operations",
                columns: new[] { "status", "lease_expires_at", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ux_environment_power_operations_active",
                table: "environment_power_operations",
                column: "environment_id",
                unique: true,
                filter: "status NOT IN ('completed', 'failed', 'cancelled')");

            migrationBuilder.CreateIndex(
                name: "IX_environment_retained_addresses_environment_id",
                table: "environment_retained_addresses",
                column: "environment_id");

            migrationBuilder.CreateIndex(
                name: "IX_environment_retained_addresses_organization_id_provider_acc~",
                table: "environment_retained_addresses",
                columns: new[] { "organization_id", "provider_account_id" });

            migrationBuilder.CreateIndex(
                name: "ux_environment_retained_addresses_live",
                table: "environment_retained_addresses",
                column: "environment_id",
                unique: true,
                filter: "state <> 'released'");

            migrationBuilder.CreateIndex(
                name: "ux_environment_retained_addresses_live_ipv4",
                table: "environment_retained_addresses",
                columns: new[] { "provider_account_id", "ipv4" },
                unique: true,
                filter: "state <> 'released'");

            migrationBuilder.CreateIndex(
                name: "ux_environment_retained_addresses_provider_id",
                table: "environment_retained_addresses",
                columns: new[] { "provider_account_id", "provider_address_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_environment_snapshots_environment_id",
                table: "environment_snapshots",
                column: "environment_id");

            migrationBuilder.CreateIndex(
                name: "IX_environment_snapshots_organization_id_provider_account_id",
                table: "environment_snapshots",
                columns: new[] { "organization_id", "provider_account_id" });

            migrationBuilder.CreateIndex(
                name: "ux_environment_snapshots_current",
                table: "environment_snapshots",
                column: "environment_id",
                unique: true,
                filter: "status = 'current'");

            migrationBuilder.Sql("""
                INSERT INTO environment_sleep_settings (environment_id, revision, state_changed_at)
                SELECT id, 0, updated_at FROM apps;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "environment_power_operations");

            migrationBuilder.DropTable(
                name: "environment_retained_addresses");

            migrationBuilder.DropTable(
                name: "environment_sleep_settings");

            migrationBuilder.DropTable(
                name: "environment_snapshots");
        }
    }
}
