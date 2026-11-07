using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Minicloud.Common.Migrations
{
    /// <inheritdoc />
    public partial class AddAppSecrets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "app_secrets",
                columns: table => new
                {
                    id = table.Column<string>(nullable: false),
                    organization_id = table.Column<string>(nullable: false),
                    app_id = table.Column<string>(nullable: false),
                    name = table.Column<string>(nullable: false),
                    encrypted_value = table.Column<string>(nullable: false),
                    created_at = table.Column<DateTimeOffset>(nullable: false),
                    updated_at = table.Column<DateTimeOffset>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_secrets", x => x.id);
                    table.ForeignKey(
                        name: "FK_app_secrets_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_app_secrets_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_app_secrets_app_id_name",
                table: "app_secrets",
                columns: new[] { "app_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_app_secrets_organization_id",
                table: "app_secrets",
                column: "organization_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_secrets");
        }
    }
}
