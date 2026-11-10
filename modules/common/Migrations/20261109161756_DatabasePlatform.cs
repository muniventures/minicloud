using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Minicloud.Common.Migrations
{
    /// <inheritdoc />
    public partial class DatabasePlatform : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_apps_organization_id_id",
                table: "apps",
                columns: new[] { "organization_id", "id" });

            migrationBuilder.CreateTable(
                name: "database_backup_artifacts",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    database_id = table.Column<string>(type: "text", nullable: false),
                    repository_app_id = table.Column<string>(type: "text", nullable: false),
                    backup_id = table.Column<string>(type: "text", nullable: false),
                    key_version = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "text", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_database_backup_artifacts", x => x.id);
                    table.ForeignKey(
                        name: "FK_database_backup_artifacts_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "database_backup_policies",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    database_id = table.Column<string>(type: "text", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    keep_last_copies = table.Column<bool>(type: "boolean", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    frequency_hours = table.Column<int>(type: "integer", nullable: false),
                    retention_days = table.Column<int>(type: "integer", nullable: false),
                    minimum_copies = table.Column<int>(type: "integer", nullable: false),
                    repository_app_id = table.Column<string>(type: "text", nullable: true),
                    next_due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    encrypted_key_material_json = table.Column<string>(type: "text", nullable: true),
                    key_version = table.Column<string>(type: "text", nullable: true),
                    last_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_success_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error_code = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_database_backup_policies", x => x.id);
                    table.ForeignKey(
                        name: "FK_database_backup_policies_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "database_operations",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    backup_policy_id = table.Column<string>(type: "text", nullable: true),
                    scheduled_policy_revision = table.Column<long>(type: "bigint", nullable: true),
                    database_id = table.Column<string>(type: "text", nullable: true),
                    server_id = table.Column<string>(type: "text", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    actor_json = table.Column<string>(type: "text", nullable: false),
                    request_json = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    ownership_token = table.Column<string>(type: "text", nullable: true),
                    error_code = table.Column<string>(type: "text", nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    result_json = table.Column<string>(type: "text", nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_database_operations", x => x.id);
                    table.ForeignKey(
                        name: "FK_database_operations_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "managed_database_networks",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    provider_account_id = table.Column<string>(type: "text", nullable: false),
                    provider_account_revision = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    region = table.Column<string>(type: "text", nullable: false),
                    ip_range = table.Column<string>(type: "text", nullable: false),
                    subnet_range = table.Column<string>(type: "text", nullable: false),
                    network_zone = table.Column<string>(type: "text", nullable: true),
                    provider_network_id = table.Column<string>(type: "text", nullable: true),
                    creation_attempted = table.Column<bool>(type: "boolean", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_managed_database_networks", x => x.id);
                    table.UniqueConstraint("AK_managed_database_networks_organization_id_id", x => new { x.organization_id, x.id });
                    table.ForeignKey(
                        name: "FK_managed_database_networks_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_managed_database_networks_provider_accounts_organization_id~",
                        columns: x => new { x.organization_id, x.provider_account_id },
                        principalTable: "provider_accounts",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "managed_database_servers",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    mode = table.Column<string>(type: "text", nullable: false),
                    engine = table.Column<string>(type: "text", nullable: false),
                    engine_version = table.Column<string>(type: "text", nullable: false),
                    plan_id = table.Column<string>(type: "text", nullable: false),
                    runtime_app_id = table.Column<string>(type: "text", nullable: true),
                    runtime_server_id = table.Column<string>(type: "text", nullable: true),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    runtime_deployment_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_managed_database_servers", x => x.id);
                    table.UniqueConstraint("AK_managed_database_servers_organization_id_id", x => new { x.organization_id, x.id });
                    table.ForeignKey(
                        name: "FK_managed_database_servers_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_managed_database_servers_provider_plans_organization_id_pla~",
                        columns: x => new { x.organization_id, x.plan_id },
                        principalTable: "provider_plans",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "managed_databases",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    engine = table.Column<string>(type: "text", nullable: false),
                    engine_version = table.Column<string>(type: "text", nullable: false),
                    placement = table.Column<string>(type: "text", nullable: false),
                    server_id = table.Column<string>(type: "text", nullable: true),
                    recovery_source_app_id = table.Column<string>(type: "text", nullable: true),
                    app_id = table.Column<string>(type: "text", nullable: true),
                    access_mode = table.Column<string>(type: "text", nullable: false),
                    network_id = table.Column<string>(type: "text", nullable: true),
                    private_ip = table.Column<string>(type: "text", nullable: true),
                    allowed_cidrs_json = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    encrypted_credentials_json = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_managed_databases", x => x.id);
                    table.UniqueConstraint("AK_managed_databases_organization_id_id", x => new { x.organization_id, x.id });
                    table.ForeignKey(
                        name: "FK_managed_databases_managed_database_networks_organization_id~",
                        columns: x => new { x.organization_id, x.network_id },
                        principalTable: "managed_database_networks",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_managed_databases_managed_database_servers_organization_id_~",
                        columns: x => new { x.organization_id, x.server_id },
                        principalTable: "managed_database_servers",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_managed_databases_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "managed_database_bindings",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    database_id = table.Column<string>(type: "text", nullable: false),
                    app_id = table.Column<string>(type: "text", nullable: false),
                    service_name = table.Column<string>(type: "text", nullable: false),
                    host = table.Column<string>(type: "text", nullable: true),
                    port = table.Column<int>(type: "integer", nullable: false),
                    ssl_mode = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_managed_database_bindings", x => x.id);
                    table.ForeignKey(
                        name: "FK_managed_database_bindings_apps_organization_id_app_id",
                        columns: x => new { x.organization_id, x.app_id },
                        principalTable: "apps",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_managed_database_bindings_managed_databases_organization_id~",
                        columns: x => new { x.organization_id, x.database_id },
                        principalTable: "managed_databases",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_database_backup_artifacts_organization_id",
                table: "database_backup_artifacts",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_database_backup_policies_database_id",
                table: "database_backup_policies",
                column: "database_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_database_backup_policies_organization_id",
                table: "database_backup_policies",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_database_operations_organization_id",
                table: "database_operations",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_managed_database_bindings_app_id_service_name",
                table: "managed_database_bindings",
                columns: new[] { "app_id", "service_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_managed_database_bindings_organization_id_app_id",
                table: "managed_database_bindings",
                columns: new[] { "organization_id", "app_id" });

            migrationBuilder.CreateIndex(
                name: "IX_managed_database_bindings_organization_id_database_id",
                table: "managed_database_bindings",
                columns: new[] { "organization_id", "database_id" });

            migrationBuilder.CreateIndex(
                name: "IX_managed_database_networks_organization_id_name",
                table: "managed_database_networks",
                columns: new[] { "organization_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_managed_database_networks_organization_id_provider_account_~",
                table: "managed_database_networks",
                columns: new[] { "organization_id", "provider_account_id" });

            migrationBuilder.CreateIndex(
                name: "IX_managed_database_networks_provider_account_id_provider_netw~",
                table: "managed_database_networks",
                columns: new[] { "provider_account_id", "provider_network_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_managed_database_servers_organization_id",
                table: "managed_database_servers",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_managed_database_servers_organization_id_name",
                table: "managed_database_servers",
                columns: new[] { "organization_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_managed_database_servers_organization_id_plan_id",
                table: "managed_database_servers",
                columns: new[] { "organization_id", "plan_id" });

            migrationBuilder.CreateIndex(
                name: "IX_managed_databases_app_id",
                table: "managed_databases",
                column: "app_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_managed_databases_organization_id",
                table: "managed_databases",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_managed_databases_organization_id_network_id",
                table: "managed_databases",
                columns: new[] { "organization_id", "network_id" });

            migrationBuilder.CreateIndex(
                name: "IX_managed_databases_organization_id_server_id",
                table: "managed_databases",
                columns: new[] { "organization_id", "server_id" });

            migrationBuilder.CreateIndex(
                name: "IX_managed_databases_server_id_name",
                table: "managed_databases",
                columns: new[] { "server_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "database_backup_artifacts");

            migrationBuilder.DropTable(
                name: "database_backup_policies");

            migrationBuilder.DropTable(
                name: "database_operations");

            migrationBuilder.DropTable(
                name: "managed_database_bindings");

            migrationBuilder.DropTable(
                name: "managed_databases");

            migrationBuilder.DropTable(
                name: "managed_database_networks");

            migrationBuilder.DropTable(
                name: "managed_database_servers");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_apps_organization_id_id",
                table: "apps");
        }
    }
}
