using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Minicloud.Common.Migrations
{
    /// <inheritdoc />
    public partial class AppOwnedCustomDomains : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "app_id",
                table: "custom_domain_registrations",
                type: "text",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE custom_domain_registrations
                SET app_id = (SELECT MIN(d.app_id) FROM domains d
                  JOIN apps a ON a.id = d.app_id AND a.organization_id = d.organization_id
                  WHERE d.organization_id = custom_domain_registrations.organization_id
                    AND d.verification_token = custom_domain_registrations.verification_token
                  HAVING COUNT(DISTINCT d.app_id) = 1);
                UPDATE custom_domain_registrations SET status = 'pending',
                  failure_message = 'Choose an application and verify domain ownership before using this domain.'
                WHERE app_id IS NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_custom_domain_registrations_app_id",
                table: "custom_domain_registrations",
                column: "app_id");

            migrationBuilder.AddForeignKey(
                name: "FK_custom_domain_registrations_apps_app_id",
                table: "custom_domain_registrations",
                column: "app_id",
                principalTable: "apps",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_custom_domain_registrations_apps_app_id",
                table: "custom_domain_registrations");

            migrationBuilder.DropIndex(
                name: "IX_custom_domain_registrations_app_id",
                table: "custom_domain_registrations");

            migrationBuilder.DropColumn(
                name: "app_id",
                table: "custom_domain_registrations");
        }
    }
}
