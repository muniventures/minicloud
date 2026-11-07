using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Minicloud.Common.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomDomainRegistrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "custom_domain_registrations",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    hostname = table.Column<string>(type: "text", nullable: false),
                    verification_token = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    failure_message = table.Column<string>(type: "text", nullable: true),
                    last_checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_custom_domain_registrations", x => x.id);
                    table.ForeignKey(
                        name: "FK_custom_domain_registrations_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_custom_domain_registrations_hostname",
                table: "custom_domain_registrations",
                column: "hostname",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_custom_domain_registrations_organization_id",
                table: "custom_domain_registrations",
                column: "organization_id");
            migrationBuilder.Sql("""
                INSERT INTO custom_domain_registrations
                    (id, organization_id, hostname, verification_token, status, created_at, updated_at)
                SELECT 'cdr_' || id, organization_id, hostname, verification_token,
                    CASE WHEN dns_status = 'verified' THEN 'verified' ELSE 'pending' END, created_at, updated_at
                FROM domains WHERE kind = 'custom_domain' AND verification_token IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "custom_domain_registrations");
        }
    }
}
