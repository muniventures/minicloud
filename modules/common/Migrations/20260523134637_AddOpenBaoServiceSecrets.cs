using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Minicloud.Common.Migrations
{
    /// <inheritdoc />
    public partial class AddOpenBaoServiceSecrets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "secret_env_json",
                table: "deployment_services",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "app_service_secrets",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    app_id = table.Column<string>(type: "text", nullable: false),
                    service_name = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    store_provider = table.Column<string>(type: "text", nullable: false),
                    store_path = table.Column<string>(type: "text", nullable: false),
                    store_version = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_service_secrets", x => x.id);
                    table.ForeignKey(
                        name: "FK_app_service_secrets_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_app_service_secrets_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_app_service_secrets_app_id_service_name",
                table: "app_service_secrets",
                columns: new[] { "app_id", "service_name" });

            migrationBuilder.CreateIndex(
                name: "IX_app_service_secrets_app_id_service_name_name",
                table: "app_service_secrets",
                columns: new[] { "app_id", "service_name", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_app_service_secrets_organization_id_app_id",
                table: "app_service_secrets",
                columns: new[] { "organization_id", "app_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_service_secrets");

            migrationBuilder.DropColumn(
                name: "secret_env_json",
                table: "deployment_services");
        }
    }
}
