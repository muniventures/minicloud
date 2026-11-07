using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Minicloud.Common.Migrations
{
    /// <inheritdoc />
    public partial class InitialControlPlane : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "organizations",
                columns: table => new
                {
                    id = table.Column<string>(nullable: false),
                    name = table.Column<string>(nullable: false),
                    slug = table.Column<string>(nullable: false),
                    created_at = table.Column<DateTimeOffset>(nullable: false),
                    updated_at = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organizations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<string>(nullable: false),
                    firebase_uid = table.Column<string>(nullable: false),
                    email = table.Column<string>(nullable: false),
                    display_name = table.Column<string>(nullable: true),
                    created_at = table.Column<DateTimeOffset>(nullable: false),
                    updated_at = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "api_keys",
                columns: table => new
                {
                    id = table.Column<string>(nullable: false),
                    organization_id = table.Column<string>(nullable: false),
                    name = table.Column<string>(nullable: false),
                    key_prefix = table.Column<string>(nullable: false),
                    key_hash = table.Column<string>(nullable: false),
                    scopes_json = table.Column<string>(nullable: false),
                    last_used_at = table.Column<DateTimeOffset>(nullable: true),
                    expires_at = table.Column<DateTimeOffset>(nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(nullable: true),
                    created_at = table.Column<DateTimeOffset>(nullable: false),
                    updated_at = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_keys", x => x.id);
                    table.ForeignKey(
                        name: "FK_api_keys_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "apps",
                columns: table => new
                {
                    id = table.Column<string>(nullable: false),
                    organization_id = table.Column<string>(nullable: false),
                    name = table.Column<string>(nullable: false),
                    slug = table.Column<string>(nullable: false),
                    plan = table.Column<string>(nullable: false),
                    database = table.Column<string>(nullable: false),
                    created_at = table.Column<DateTimeOffset>(nullable: false),
                    updated_at = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_apps", x => x.id);
                    table.ForeignKey(
                        name: "FK_apps_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "organization_members",
                columns: table => new
                {
                    id = table.Column<string>(nullable: false),
                    organization_id = table.Column<string>(nullable: false),
                    user_id = table.Column<string>(nullable: false),
                    role = table.Column<string>(nullable: false),
                    created_at = table.Column<DateTimeOffset>(nullable: false),
                    updated_at = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_members", x => x.id);
                    table.ForeignKey(
                        name: "FK_organization_members_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_organization_members_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "deployments",
                columns: table => new
                {
                    id = table.Column<string>(nullable: false),
                    organization_id = table.Column<string>(nullable: false),
                    app_id = table.Column<string>(nullable: false),
                    database = table.Column<string>(nullable: false),
                    status = table.Column<string>(nullable: false),
                    domain = table.Column<string>(nullable: true),
                    website_url = table.Column<string>(nullable: true),
                    github_run_id = table.Column<string>(nullable: true),
                    github_run_url = table.Column<string>(nullable: true),
                    commit_sha = table.Column<string>(nullable: true),
                    failure_code = table.Column<string>(nullable: true),
                    failure_message = table.Column<string>(nullable: true),
                    created_at = table.Column<DateTimeOffset>(nullable: false),
                    started_at = table.Column<DateTimeOffset>(nullable: true),
                    completed_at = table.Column<DateTimeOffset>(nullable: true),
                    updated_at = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deployments", x => x.id);
                    table.ForeignKey(
                        name: "FK_deployments_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_deployments_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "domains",
                columns: table => new
                {
                    id = table.Column<string>(nullable: false),
                    app_id = table.Column<string>(nullable: false),
                    hostname = table.Column<string>(nullable: false),
                    target_ip = table.Column<string>(nullable: true),
                    ssl_status = table.Column<string>(nullable: false),
                    created_at = table.Column<DateTimeOffset>(nullable: false),
                    updated_at = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_domains", x => x.id);
                    table.ForeignKey(
                        name: "FK_domains_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "server_instances",
                columns: table => new
                {
                    id = table.Column<string>(nullable: false),
                    organization_id = table.Column<string>(nullable: false),
                    app_id = table.Column<string>(nullable: false),
                    provider = table.Column<string>(nullable: false),
                    provider_server_id = table.Column<string>(nullable: true),
                    ip_address = table.Column<string>(nullable: true),
                    region = table.Column<string>(nullable: false),
                    status = table.Column<string>(nullable: false),
                    bootstrap_status = table.Column<string>(nullable: false),
                    created_at = table.Column<DateTimeOffset>(nullable: false),
                    updated_at = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_server_instances", x => x.id);
                    table.ForeignKey(
                        name: "FK_server_instances_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_server_instances_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "deployment_events",
                columns: table => new
                {
                    id = table.Column<string>(nullable: false),
                    deployment_id = table.Column<string>(nullable: false),
                    level = table.Column<string>(nullable: false),
                    message = table.Column<string>(nullable: false),
                    metadata_json = table.Column<string>(nullable: true),
                    created_at = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deployment_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_deployment_events_deployments_deployment_id",
                        column: x => x.deployment_id,
                        principalTable: "deployments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "deployment_logs",
                columns: table => new
                {
                    id = table.Column<string>(nullable: false),
                    deployment_id = table.Column<string>(nullable: false),
                    source = table.Column<string>(nullable: false),
                    content = table.Column<string>(nullable: false),
                    created_at = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deployment_logs", x => x.id);
                    table.ForeignKey(
                        name: "FK_deployment_logs_deployments_deployment_id",
                        column: x => x.deployment_id,
                        principalTable: "deployments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "deployment_services",
                columns: table => new
                {
                    id = table.Column<string>(nullable: false),
                    deployment_id = table.Column<string>(nullable: false),
                    name = table.Column<string>(nullable: false),
                    image = table.Column<string>(nullable: false),
                    port = table.Column<int>(nullable: false),
                    @public = table.Column<bool>(name: "public", nullable: false),
                    path = table.Column<string>(nullable: false),
                    health_path = table.Column<string>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deployment_services", x => x.id);
                    table.ForeignKey(
                        name: "FK_deployment_services_deployments_deployment_id",
                        column: x => x.deployment_id,
                        principalTable: "deployments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_key_prefix",
                table: "api_keys",
                column: "key_prefix");

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_organization_id_revoked_at",
                table: "api_keys",
                columns: new[] { "organization_id", "revoked_at" });

            migrationBuilder.CreateIndex(
                name: "IX_apps_organization_id_slug",
                table: "apps",
                columns: new[] { "organization_id", "slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_deployment_events_deployment_id_created_at",
                table: "deployment_events",
                columns: new[] { "deployment_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_deployment_logs_deployment_id_created_at",
                table: "deployment_logs",
                columns: new[] { "deployment_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_deployment_services_deployment_id_name",
                table: "deployment_services",
                columns: new[] { "deployment_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_deployments_app_id_created_at",
                table: "deployments",
                columns: new[] { "app_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_deployments_organization_id_created_at",
                table: "deployments",
                columns: new[] { "organization_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_deployments_status_created_at",
                table: "deployments",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_domains_app_id",
                table: "domains",
                column: "app_id");

            migrationBuilder.CreateIndex(
                name: "IX_domains_hostname",
                table: "domains",
                column: "hostname",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_organization_members_organization_id_user_id",
                table: "organization_members",
                columns: new[] { "organization_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_organization_members_user_id",
                table: "organization_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_organizations_slug",
                table: "organizations",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_server_instances_app_id",
                table: "server_instances",
                column: "app_id");

            migrationBuilder.CreateIndex(
                name: "IX_server_instances_organization_id",
                table: "server_instances",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_users_email",
                table: "users",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_firebase_uid",
                table: "users",
                column: "firebase_uid",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_keys");

            migrationBuilder.DropTable(
                name: "deployment_events");

            migrationBuilder.DropTable(
                name: "deployment_logs");

            migrationBuilder.DropTable(
                name: "deployment_services");

            migrationBuilder.DropTable(
                name: "domains");

            migrationBuilder.DropTable(
                name: "organization_members");

            migrationBuilder.DropTable(
                name: "server_instances");

            migrationBuilder.DropTable(
                name: "deployments");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "apps");

            migrationBuilder.DropTable(
                name: "organizations");
        }
    }
}
