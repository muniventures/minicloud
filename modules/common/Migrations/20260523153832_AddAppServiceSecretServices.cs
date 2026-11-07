using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Minicloud.Common.Migrations
{
    /// <inheritdoc />
    public partial class AddAppServiceSecretServices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "app_service_secret_services",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    app_id = table.Column<string>(type: "text", nullable: false),
                    service_name = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_service_secret_services", x => x.id);
                    table.ForeignKey(
                        name: "FK_app_service_secret_services_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_app_service_secret_services_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_app_service_secret_services_app_id_service_name",
                table: "app_service_secret_services",
                columns: new[] { "app_id", "service_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_app_service_secret_services_organization_id_app_id",
                table: "app_service_secret_services",
                columns: new[] { "organization_id", "app_id" });

            if (ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite")
            {
                migrationBuilder.Sql("""
                    INSERT INTO app_service_secret_services (id, organization_id, app_id, service_name, created_at, updated_at, deleted_at)
                    SELECT
                        'svc_' || lower(hex(randomblob(16))),
                        organization_id,
                        app_id,
                        service_name,
                        min(created_at),
                        max(updated_at),
                        NULL
                    FROM app_service_secrets
                    WHERE deleted_at IS NULL
                    GROUP BY organization_id, app_id, service_name
                    """);
            }
            else
            {
                migrationBuilder.Sql("""
                    INSERT INTO app_service_secret_services (id, organization_id, app_id, service_name, created_at, updated_at, deleted_at)
                    SELECT
                        'svc_' || md5(organization_id || ':' || app_id || ':' || service_name),
                        organization_id,
                        app_id,
                        service_name,
                        min(created_at),
                        max(updated_at),
                        NULL
                    FROM app_service_secrets
                    WHERE deleted_at IS NULL
                    GROUP BY organization_id, app_id, service_name
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_service_secret_services");
        }
    }
}
