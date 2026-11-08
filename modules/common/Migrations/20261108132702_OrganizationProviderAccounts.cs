using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Minicloud.Common.Migrations
{
    /// <inheritdoc />
    public partial class OrganizationProviderAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_server_instances_organization_id",
                table: "server_instances");

            migrationBuilder.DropIndex(
                name: "IX_runtime_servers_organization_id",
                table: "runtime_servers");

            migrationBuilder.DropIndex(
                name: "IX_runtime_servers_provider_provider_server_id",
                table: "runtime_servers");

            migrationBuilder.DropIndex(
                name: "IX_app_storage_volumes_provider_volume_id",
                table: "app_storage_volumes");

            migrationBuilder.AddColumn<string>(
                name: "image",
                table: "server_instances",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "plan_id",
                table: "server_instances",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provider_account_id",
                table: "server_instances",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sku",
                table: "server_instances",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provider_account_id",
                table: "runtime_servers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "plan_id",
                table: "deployments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "plan_revision",
                table: "deployments",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provider_account_id",
                table: "deployments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provider_build_platform",
                table: "deployments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "provider_creation_attempted",
                table: "deployments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "provider_image",
                table: "deployments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provider_region",
                table: "deployments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provider_sku",
                table: "deployments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provider_type",
                table: "deployments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "plan_id",
                table: "apps",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "organization_id",
                table: "app_storage_volumes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provider",
                table: "app_storage_volumes",
                type: "text",
                nullable: false,
                defaultValue: "hetzner");

            migrationBuilder.AddColumn<string>(
                name: "provider_account_id",
                table: "app_storage_volumes",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "provider_accounts",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    provider_type = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    project_id = table.Column<string>(type: "text", nullable: true),
                    default_os_id = table.Column<int>(type: "integer", nullable: true),
                    credential_store_provider = table.Column<string>(type: "text", nullable: false),
                    credential_path = table.Column<string>(type: "text", nullable: false),
                    credential_version = table.Column<string>(type: "text", nullable: true),
                    credential_generation = table.Column<string>(type: "text", nullable: false),
                    validation_status = table.Column<string>(type: "text", nullable: false),
                    last_validated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_accounts", x => x.id);
                    table.UniqueConstraint("AK_provider_accounts_organization_id_id", x => new { x.organization_id, x.id });
                    table.ForeignKey(
                        name: "FK_provider_accounts_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "provider_credential_cleanups",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    provider_account_id = table.Column<string>(type: "text", nullable: false),
                    store_provider = table.Column<string>(type: "text", nullable: false),
                    path = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<string>(type: "text", nullable: true),
                    not_before = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_credential_cleanups", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "provider_plan_names",
                columns: table => new
                {
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    plan_id = table.Column<string>(type: "text", nullable: false),
                    retired = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_plan_names", x => new { x.organization_id, x.normalized_name });
                });

            migrationBuilder.CreateTable(
                name: "provider_plans",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    provider_account_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    region = table.Column<string>(type: "text", nullable: false),
                    sku = table.Column<string>(type: "text", nullable: false),
                    image = table.Column<string>(type: "text", nullable: true),
                    build_platform = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    provisioning_locked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_plans", x => x.id);
                    table.UniqueConstraint("AK_provider_plans_organization_id_id", x => new { x.organization_id, x.id });
                    table.ForeignKey(
                        name: "FK_provider_plans_provider_accounts_organization_id_provider_a~",
                        columns: x => new { x.organization_id, x.provider_account_id },
                        principalTable: "provider_accounts",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_server_instances_organization_id_provider_account_id",
                table: "server_instances",
                columns: new[] { "organization_id", "provider_account_id" });

            migrationBuilder.CreateIndex(
                name: "IX_runtime_servers_organization_id_provider_account_id",
                table: "runtime_servers",
                columns: new[] { "organization_id", "provider_account_id" });

            migrationBuilder.CreateIndex(
                name: "IX_runtime_servers_provider_account_id_provider_provider_serve~",
                table: "runtime_servers",
                columns: new[] { "provider_account_id", "provider", "provider_server_id" });

            migrationBuilder.CreateIndex(
                name: "IX_deployments_organization_id_provider_account_id",
                table: "deployments",
                columns: new[] { "organization_id", "provider_account_id" });

            migrationBuilder.CreateIndex(
                name: "IX_apps_organization_id_plan_id",
                table: "apps",
                columns: new[] { "organization_id", "plan_id" });

            migrationBuilder.CreateIndex(
                name: "IX_app_storage_volumes_organization_id_provider_account_id",
                table: "app_storage_volumes",
                columns: new[] { "organization_id", "provider_account_id" });

            migrationBuilder.CreateIndex(
                name: "IX_app_storage_volumes_provider_account_id_provider_volume_id",
                table: "app_storage_volumes",
                columns: new[] { "provider_account_id", "provider_volume_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_plans_organization_id_normalized_name",
                table: "provider_plans",
                columns: new[] { "organization_id", "normalized_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_plans_organization_id_provider_account_id",
                table: "provider_plans",
                columns: new[] { "organization_id", "provider_account_id" });

            migrationBuilder.AddForeignKey(
                name: "FK_app_storage_volumes_provider_accounts_organization_id_provi~",
                table: "app_storage_volumes",
                columns: new[] { "organization_id", "provider_account_id" },
                principalTable: "provider_accounts",
                principalColumns: new[] { "organization_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_apps_provider_plans_organization_id_plan_id",
                table: "apps",
                columns: new[] { "organization_id", "plan_id" },
                principalTable: "provider_plans",
                principalColumns: new[] { "organization_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_deployments_provider_accounts_organization_id_provider_acco~",
                table: "deployments",
                columns: new[] { "organization_id", "provider_account_id" },
                principalTable: "provider_accounts",
                principalColumns: new[] { "organization_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_runtime_servers_provider_accounts_organization_id_provider_~",
                table: "runtime_servers",
                columns: new[] { "organization_id", "provider_account_id" },
                principalTable: "provider_accounts",
                principalColumns: new[] { "organization_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_server_instances_provider_accounts_organization_id_provider~",
                table: "server_instances",
                columns: new[] { "organization_id", "provider_account_id" },
                principalTable: "provider_accounts",
                principalColumns: new[] { "organization_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_app_storage_volumes_provider_accounts_organization_id_provi~",
                table: "app_storage_volumes");

            migrationBuilder.DropForeignKey(
                name: "FK_apps_provider_plans_organization_id_plan_id",
                table: "apps");

            migrationBuilder.DropForeignKey(
                name: "FK_deployments_provider_accounts_organization_id_provider_acco~",
                table: "deployments");

            migrationBuilder.DropForeignKey(
                name: "FK_runtime_servers_provider_accounts_organization_id_provider_~",
                table: "runtime_servers");

            migrationBuilder.DropForeignKey(
                name: "FK_server_instances_provider_accounts_organization_id_provider~",
                table: "server_instances");

            migrationBuilder.DropTable(
                name: "provider_credential_cleanups");

            migrationBuilder.DropTable(
                name: "provider_plan_names");

            migrationBuilder.DropTable(
                name: "provider_plans");

            migrationBuilder.DropTable(
                name: "provider_accounts");

            migrationBuilder.DropIndex(
                name: "IX_server_instances_organization_id_provider_account_id",
                table: "server_instances");

            migrationBuilder.DropIndex(
                name: "IX_runtime_servers_organization_id_provider_account_id",
                table: "runtime_servers");

            migrationBuilder.DropIndex(
                name: "IX_runtime_servers_provider_account_id_provider_provider_serve~",
                table: "runtime_servers");

            migrationBuilder.DropIndex(
                name: "IX_deployments_organization_id_provider_account_id",
                table: "deployments");

            migrationBuilder.DropIndex(
                name: "IX_apps_organization_id_plan_id",
                table: "apps");

            migrationBuilder.DropIndex(
                name: "IX_app_storage_volumes_organization_id_provider_account_id",
                table: "app_storage_volumes");

            migrationBuilder.DropIndex(
                name: "IX_app_storage_volumes_provider_account_id_provider_volume_id",
                table: "app_storage_volumes");

            migrationBuilder.DropColumn(
                name: "image",
                table: "server_instances");

            migrationBuilder.DropColumn(
                name: "plan_id",
                table: "server_instances");

            migrationBuilder.DropColumn(
                name: "provider_account_id",
                table: "server_instances");

            migrationBuilder.DropColumn(
                name: "sku",
                table: "server_instances");

            migrationBuilder.DropColumn(
                name: "provider_account_id",
                table: "runtime_servers");

            migrationBuilder.DropColumn(
                name: "plan_id",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "plan_revision",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "provider_account_id",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "provider_build_platform",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "provider_creation_attempted",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "provider_image",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "provider_region",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "provider_sku",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "provider_type",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "plan_id",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "organization_id",
                table: "app_storage_volumes");

            migrationBuilder.DropColumn(
                name: "provider",
                table: "app_storage_volumes");

            migrationBuilder.DropColumn(
                name: "provider_account_id",
                table: "app_storage_volumes");

            migrationBuilder.CreateIndex(
                name: "IX_server_instances_organization_id",
                table: "server_instances",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_runtime_servers_organization_id",
                table: "runtime_servers",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_runtime_servers_provider_provider_server_id",
                table: "runtime_servers",
                columns: new[] { "provider", "provider_server_id" });

            migrationBuilder.CreateIndex(
                name: "IX_app_storage_volumes_provider_volume_id",
                table: "app_storage_volumes",
                column: "provider_volume_id",
                unique: true);
        }
    }
}
