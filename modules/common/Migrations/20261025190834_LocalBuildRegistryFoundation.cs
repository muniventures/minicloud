using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Minicloud.Common.Migrations
{
    /// <inheritdoc />
    public partial class LocalBuildRegistryFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "build_platform",
                table: "server_instances",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "build_platform_observed_at",
                table: "server_instances",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "image_digest",
                table: "deployment_services",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "image_platform",
                table: "deployment_services",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "image_publication_id",
                table: "deployment_services",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "image_publications",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    app_id = table.Column<string>(type: "text", nullable: false),
                    service_name = table.Column<string>(type: "text", nullable: false),
                    idempotency_key = table.Column<string>(type: "text", nullable: false),
                    repository = table.Column<string>(type: "text", nullable: false),
                    candidate_tag = table.Column<string>(type: "text", nullable: false),
                    target_version = table.Column<string>(type: "text", nullable: false),
                    platform = table.Column<string>(type: "text", nullable: false),
                    input_sha256 = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    manifest_digest = table.Column<string>(type: "text", nullable: true),
                    manifest_json = table.Column<string>(type: "text", nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    failure_code = table.Column<string>(type: "text", nullable: true),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ready_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    retired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_image_publications", x => x.id);
                    table.ForeignKey(
                        name: "FK_image_publications_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "registry_maintenance_states",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    writes_blocked = table.Column<bool>(type: "boolean", nullable: false),
                    owner_token = table.Column<string>(type: "text", nullable: true),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_registry_maintenance_states", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "registry_sessions",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    app_id = table.Column<string>(type: "text", nullable: false),
                    image_publication_id = table.Column<string>(type: "text", nullable: true),
                    deployment_id = table.Column<string>(type: "text", nullable: true),
                    server_id = table.Column<string>(type: "text", nullable: true),
                    ownership_token = table.Column<string>(type: "text", nullable: true),
                    subject_id = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    credential_hash = table.Column<string>(type: "text", nullable: false),
                    reserved_bytes = table.Column<long>(type: "bigint", nullable: false),
                    charged_bytes = table.Column<long>(type: "bigint", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reclaimed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_registry_sessions", x => x.id);
                    table.ForeignKey(
                        name: "FK_registry_sessions_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_registry_sessions_deployments_deployment_id",
                        column: x => x.deployment_id,
                        principalTable: "deployments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_registry_sessions_image_publications_image_publication_id",
                        column: x => x.image_publication_id,
                        principalTable: "image_publications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_deployment_services_image_publication_id",
                table: "deployment_services",
                column: "image_publication_id");

            migrationBuilder.CreateIndex(
                name: "IX_image_publications_app_id_idempotency_key",
                table: "image_publications",
                columns: new[] { "app_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_image_publications_organization_id_app_id_service_name",
                table: "image_publications",
                columns: new[] { "organization_id", "app_id", "service_name" });

            migrationBuilder.CreateIndex(
                name: "IX_image_publications_repository_candidate_tag",
                table: "image_publications",
                columns: new[] { "repository", "candidate_tag" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_image_publications_status_expires_at",
                table: "image_publications",
                columns: new[] { "status", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "IX_registry_sessions_app_id",
                table: "registry_sessions",
                column: "app_id");

            migrationBuilder.CreateIndex(
                name: "IX_registry_sessions_credential_hash",
                table: "registry_sessions",
                column: "credential_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_registry_sessions_deployment_id",
                table: "registry_sessions",
                column: "deployment_id");

            migrationBuilder.CreateIndex(
                name: "IX_registry_sessions_image_publication_id",
                table: "registry_sessions",
                column: "image_publication_id");

            migrationBuilder.CreateIndex(
                name: "IX_registry_sessions_organization_id_app_id_expires_at",
                table: "registry_sessions",
                columns: new[] { "organization_id", "app_id", "expires_at" });

            migrationBuilder.AddForeignKey(
                name: "FK_deployment_services_image_publications_image_publication_id",
                table: "deployment_services",
                column: "image_publication_id",
                principalTable: "image_publications",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_deployment_services_image_publications_image_publication_id",
                table: "deployment_services");

            migrationBuilder.DropTable(
                name: "registry_maintenance_states");

            migrationBuilder.DropTable(
                name: "registry_sessions");

            migrationBuilder.DropTable(
                name: "image_publications");

            migrationBuilder.DropIndex(
                name: "IX_deployment_services_image_publication_id",
                table: "deployment_services");

            migrationBuilder.DropColumn(
                name: "build_platform",
                table: "server_instances");

            migrationBuilder.DropColumn(
                name: "build_platform_observed_at",
                table: "server_instances");

            migrationBuilder.DropColumn(
                name: "image_digest",
                table: "deployment_services");

            migrationBuilder.DropColumn(
                name: "image_platform",
                table: "deployment_services");

            migrationBuilder.DropColumn(
                name: "image_publication_id",
                table: "deployment_services");
        }
    }
}
