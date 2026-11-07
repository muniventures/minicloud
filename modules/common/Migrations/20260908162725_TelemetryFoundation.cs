using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Minicloud.Common.Migrations
{
    /// <inheritdoc />
    public partial class TelemetryFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "app_telemetry_settings",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    app_id = table.Column<string>(type: "text", nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    deleting = table.Column<bool>(type: "boolean", nullable: false),
                    retention_policy = table.Column<string>(type: "text", nullable: false),
                    last_accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    provisioning_status = table.Column<string>(type: "text", nullable: false),
                    provisioning_version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_telemetry_settings", x => x.id);
                    table.ForeignKey(
                        name: "FK_app_telemetry_settings_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_app_telemetry_settings_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    app_id = table.Column<string>(type: "text", nullable: true),
                    actor = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    details = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_audit_events_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "FK_audit_events_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "grafana_data_source_bindings",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    app_id = table.Column<string>(type: "text", nullable: false),
                    grafana_uid = table.Column<string>(type: "text", nullable: false),
                    credential_reference = table.Column<string>(type: "text", nullable: false),
                    reconciliation_status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_grafana_data_source_bindings", x => x.id);
                    table.ForeignKey(
                        name: "FK_grafana_data_source_bindings_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_grafana_data_source_bindings_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "grafana_launch_codes",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    app_id = table.Column<string>(type: "text", nullable: false),
                    minicloud_user_id = table.Column<string>(type: "text", nullable: false),
                    code_hash = table.Column<string>(type: "text", nullable: false),
                    destination = table.Column<string>(type: "text", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_grafana_launch_codes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "grafana_organization_bindings",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    grafana_org_id = table.Column<long>(type: "bigint", nullable: false),
                    reconciliation_status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_grafana_organization_bindings", x => x.id);
                    table.ForeignKey(
                        name: "FK_grafana_organization_bindings_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "grafana_proxy_sessions",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    app_id = table.Column<string>(type: "text", nullable: false),
                    minicloud_user_id = table.Column<string>(type: "text", nullable: false),
                    session_hash = table.Column<string>(type: "text", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_grafana_proxy_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "grafana_user_bindings",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    minicloud_user_id = table.Column<string>(type: "text", nullable: false),
                    grafana_user_id = table.Column<long>(type: "bigint", nullable: false),
                    expected_role = table.Column<string>(type: "text", nullable: false),
                    reconciliation_status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_grafana_user_bindings", x => x.id);
                    table.ForeignKey(
                        name: "FK_grafana_user_bindings_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "telemetry_cleanup_jobs",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    app_id = table.Column<string>(type: "text", nullable: false),
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    cutoff_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_telemetry_cleanup_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "telemetry_daily_usages",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    app_id = table.Column<string>(type: "text", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    admitted_bytes = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_telemetry_daily_usages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "telemetry_ingestion_credentials",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    app_id = table.Column<string>(type: "text", nullable: false),
                    service_name = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    key_prefix = table.Column<string>(type: "text", nullable: false),
                    key_hash = table.Column<string>(type: "text", nullable: false),
                    credential_version = table.Column<int>(type: "integer", nullable: false),
                    created_by_actor = table.Column<string>(type: "text", nullable: true),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_telemetry_ingestion_credentials", x => x.id);
                    table.ForeignKey(
                        name: "FK_telemetry_ingestion_credentials_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_telemetry_ingestion_credentials_organizations_organization_~",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_app_telemetry_settings_app_id",
                table: "app_telemetry_settings",
                column: "app_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_app_telemetry_settings_organization_id_app_id",
                table: "app_telemetry_settings",
                columns: new[] { "organization_id", "app_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_app_telemetry_settings_tenant_id",
                table: "app_telemetry_settings",
                column: "tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_app_id_created_at",
                table: "audit_events",
                columns: new[] { "app_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_organization_id_created_at",
                table: "audit_events",
                columns: new[] { "organization_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_grafana_data_source_bindings_app_id_deleted_at",
                table: "grafana_data_source_bindings",
                columns: new[] { "app_id", "deleted_at" });

            migrationBuilder.CreateIndex(
                name: "IX_grafana_data_source_bindings_grafana_uid",
                table: "grafana_data_source_bindings",
                column: "grafana_uid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_grafana_data_source_bindings_organization_id",
                table: "grafana_data_source_bindings",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_grafana_launch_codes_code_hash",
                table: "grafana_launch_codes",
                column: "code_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_grafana_launch_codes_expires_at",
                table: "grafana_launch_codes",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_grafana_organization_bindings_grafana_org_id",
                table: "grafana_organization_bindings",
                column: "grafana_org_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_grafana_organization_bindings_organization_id",
                table: "grafana_organization_bindings",
                column: "organization_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_grafana_proxy_sessions_organization_id_minicloud_user_id",
                table: "grafana_proxy_sessions",
                columns: new[] { "organization_id", "minicloud_user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_grafana_proxy_sessions_session_hash",
                table: "grafana_proxy_sessions",
                column: "session_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_grafana_user_bindings_organization_id_minicloud_user_id",
                table: "grafana_user_bindings",
                columns: new[] { "organization_id", "minicloud_user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_telemetry_cleanup_jobs_app_id_status",
                table: "telemetry_cleanup_jobs",
                columns: new[] { "app_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_telemetry_cleanup_jobs_status",
                table: "telemetry_cleanup_jobs",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_telemetry_daily_usages_app_id_day",
                table: "telemetry_daily_usages",
                columns: new[] { "app_id", "day" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_telemetry_daily_usages_organization_id_day",
                table: "telemetry_daily_usages",
                columns: new[] { "organization_id", "day" });

            migrationBuilder.CreateIndex(
                name: "IX_telemetry_ingestion_credentials_app_id_service_name_revoked~",
                table: "telemetry_ingestion_credentials",
                columns: new[] { "app_id", "service_name", "revoked_at" });

            migrationBuilder.CreateIndex(
                name: "IX_telemetry_ingestion_credentials_key_prefix",
                table: "telemetry_ingestion_credentials",
                column: "key_prefix");

            migrationBuilder.CreateIndex(
                name: "IX_telemetry_ingestion_credentials_organization_id_app_id",
                table: "telemetry_ingestion_credentials",
                columns: new[] { "organization_id", "app_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_telemetry_settings");

            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "grafana_data_source_bindings");

            migrationBuilder.DropTable(
                name: "grafana_launch_codes");

            migrationBuilder.DropTable(
                name: "grafana_organization_bindings");

            migrationBuilder.DropTable(
                name: "grafana_proxy_sessions");

            migrationBuilder.DropTable(
                name: "grafana_user_bindings");

            migrationBuilder.DropTable(
                name: "telemetry_cleanup_jobs");

            migrationBuilder.DropTable(
                name: "telemetry_daily_usages");

            migrationBuilder.DropTable(
                name: "telemetry_ingestion_credentials");
        }
    }
}
