using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Minicloud.Common.Migrations
{
    /// <inheritdoc />
    public partial class IndependentAssetReleases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "asset_release_id",
                table: "deployments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "asset_mounts_json",
                table: "deployment_services",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "asset_releases",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    app_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    artifact_id = table.Column<string>(type: "text", nullable: false),
                    input_sha256 = table.Column<string>(type: "text", nullable: false),
                    output_path = table.Column<string>(type: "text", nullable: false),
                    manifest_json = table.Column<string>(type: "text", nullable: true),
                    manifest_sha256 = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ready_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_asset_releases", x => x.id);
                    table.ForeignKey(
                        name: "FK_asset_releases_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_asset_releases_deployment_artifacts_artifact_id",
                        column: x => x.artifact_id,
                        principalTable: "deployment_artifacts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_deployments_asset_release_id",
                table: "deployments",
                column: "asset_release_id");

            migrationBuilder.CreateIndex(
                name: "IX_asset_releases_app_id_name_input_sha256",
                table: "asset_releases",
                columns: new[] { "app_id", "name", "input_sha256" });

            migrationBuilder.CreateIndex(
                name: "IX_asset_releases_artifact_id",
                table: "asset_releases",
                column: "artifact_id");

            migrationBuilder.AddForeignKey(
                name: "FK_deployments_asset_releases_asset_release_id",
                table: "deployments",
                column: "asset_release_id",
                principalTable: "asset_releases",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_deployments_asset_releases_asset_release_id",
                table: "deployments");

            migrationBuilder.DropTable(
                name: "asset_releases");

            migrationBuilder.DropIndex(
                name: "IX_deployments_asset_release_id",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "asset_release_id",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "asset_mounts_json",
                table: "deployment_services");
        }
    }
}
