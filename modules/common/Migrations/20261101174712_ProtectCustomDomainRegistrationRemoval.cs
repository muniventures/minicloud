using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Minicloud.Common.Migrations
{
    /// <inheritdoc />
    public partial class ProtectCustomDomainRegistrationRemoval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "custom_domain_registration_id",
                table: "domains",
                type: "text",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE domains SET custom_domain_registration_id = (
                  SELECT MIN(r.id) FROM custom_domain_registrations r
                  WHERE r.app_id = domains.app_id AND r.organization_id = domains.organization_id
                    AND r.verification_token = domains.verification_token
                  HAVING COUNT(*) = 1)
                WHERE kind = 'custom_domain';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_domains_custom_domain_registration_id",
                table: "domains",
                column: "custom_domain_registration_id");

            migrationBuilder.AddForeignKey(
                name: "FK_domains_custom_domain_registrations_custom_domain_registrat~",
                table: "domains",
                column: "custom_domain_registration_id",
                principalTable: "custom_domain_registrations",
                principalColumn: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_domains_custom_domain_registrations_custom_domain_registrat~",
                table: "domains");

            migrationBuilder.DropIndex(
                name: "IX_domains_custom_domain_registration_id",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "custom_domain_registration_id",
                table: "domains");
        }
    }
}
