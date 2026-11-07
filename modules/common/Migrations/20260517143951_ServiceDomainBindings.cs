using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Minicloud.Common.Migrations
{
    /// <inheritdoc />
    public partial class ServiceDomainBindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM domains");

            migrationBuilder.DropIndex(
                name: "IX_domains_app_id",
                table: "domains");

            migrationBuilder.AddColumn<string>(
                name: "apply_status",
                table: "domains",
                type: "text",
                nullable: false,
                defaultValue: "pending");

            migrationBuilder.AddColumn<string>(
                name: "dns_status",
                table: "domains",
                type: "text",
                nullable: false,
                defaultValue: "verified");

            migrationBuilder.AddColumn<string>(
                name: "failure_code",
                table: "domains",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "failure_message",
                table: "domains",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "domains",
                type: "text",
                nullable: false,
                defaultValue: "minicloud_subdomain");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_applied_at",
                table: "domains",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "organization_id",
                table: "domains",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "path_prefix",
                table: "domains",
                type: "text",
                nullable: false,
                defaultValue: "/");

            migrationBuilder.AddColumn<string>(
                name: "service_name",
                table: "domains",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "domains",
                type: "text",
                nullable: false,
                defaultValue: "pending_apply");

            migrationBuilder.CreateIndex(
                name: "IX_domains_app_id_service_name",
                table: "domains",
                columns: new[] { "app_id", "service_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_domains_organization_id",
                table: "domains",
                column: "organization_id");

            migrationBuilder.AddForeignKey(
                name: "FK_domains_organizations_organization_id",
                table: "domains",
                column: "organization_id",
                principalTable: "organizations",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_domains_organizations_organization_id",
                table: "domains");

            migrationBuilder.DropIndex(
                name: "IX_domains_app_id_service_name",
                table: "domains");

            migrationBuilder.DropIndex(
                name: "IX_domains_organization_id",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "apply_status",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "dns_status",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "failure_code",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "failure_message",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "last_applied_at",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "organization_id",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "path_prefix",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "service_name",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "status",
                table: "domains");

            migrationBuilder.CreateIndex(
                name: "IX_domains_app_id",
                table: "domains",
                column: "app_id");
        }
    }
}
