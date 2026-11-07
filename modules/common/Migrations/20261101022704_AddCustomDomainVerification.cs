using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Minicloud.Common.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomDomainVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_checked_at",
                table: "domains",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "verification_token",
                table: "domains",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_checked_at",
                table: "domains");

            migrationBuilder.DropColumn(
                name: "verification_token",
                table: "domains");
        }
    }
}
