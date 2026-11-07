using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Minicloud.Common.Migrations
{
    /// <inheritdoc />
    public partial class AddDeploymentArtifacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "artifact_id",
                table: "deployment_services",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "deployment_artifacts",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    app_id = table.Column<string>(type: "text", nullable: false),
                    service_name = table.Column<string>(type: "text", nullable: false),
                    file_name = table.Column<string>(type: "text", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    expected_size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    expected_sha256 = table.Column<string>(type: "text", nullable: true),
                    size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    sha256 = table.Column<string>(type: "text", nullable: true),
                    manifest_json = table.Column<string>(type: "text", nullable: false),
                    storage_path = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    failure_code = table.Column<string>(type: "text", nullable: true),
                    failure_message = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deployment_artifacts", x => x.id);
                    table.ForeignKey(
                        name: "FK_deployment_artifacts_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_deployment_artifacts_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_deployment_services_artifact_id",
                table: "deployment_services",
                column: "artifact_id");

            migrationBuilder.CreateIndex(
                name: "IX_deployment_artifacts_app_id",
                table: "deployment_artifacts",
                column: "app_id");

            migrationBuilder.CreateIndex(
                name: "IX_deployment_artifacts_organization_id_app_id_created_at",
                table: "deployment_artifacts",
                columns: new[] { "organization_id", "app_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_deployment_artifacts_status_expires_at",
                table: "deployment_artifacts",
                columns: new[] { "status", "expires_at" });

            migrationBuilder.AddForeignKey(
                name: "FK_deployment_services_deployment_artifacts_artifact_id",
                table: "deployment_services",
                column: "artifact_id",
                principalTable: "deployment_artifacts",
                principalColumn: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_deployment_services_deployment_artifacts_artifact_id",
                table: "deployment_services");

            migrationBuilder.DropTable(
                name: "deployment_artifacts");

            migrationBuilder.DropIndex(
                name: "IX_deployment_services_artifact_id",
                table: "deployment_services");

            migrationBuilder.DropColumn(
                name: "artifact_id",
                table: "deployment_services");
        }
    }
}
