using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Minicloud.Common.Migrations
{
    /// <inheritdoc />
    public partial class AddDeploymentOwnershipFencing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ownership_acquired_at",
                table: "deployments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ownership_token",
                table: "deployments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "worker_id",
                table: "deployments",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_deployments_app_id",
                table: "deployments",
                column: "app_id",
                unique: true,
                filter: "ownership_token IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_deployments_ownership_token",
                table: "deployments",
                column: "ownership_token",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_deployments_app_id",
                table: "deployments");

            migrationBuilder.DropIndex(
                name: "IX_deployments_ownership_token",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "ownership_acquired_at",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "ownership_token",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "worker_id",
                table: "deployments");
        }
    }
}
