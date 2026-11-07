using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Minicloud.Common.Data;

#nullable disable

namespace Minicloud.Common.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(MinicloudDbContext))]
    [Migration("20260518165000_AllowMultipleServiceDomains")]
    public partial class AllowMultipleServiceDomains : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_domains_app_id_service_name",
                table: "domains");

            migrationBuilder.CreateIndex(
                name: "IX_domains_app_id_service_name",
                table: "domains",
                columns: new[] { "app_id", "service_name" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_domains_app_id_service_name",
                table: "domains");

            migrationBuilder.CreateIndex(
                name: "IX_domains_app_id_service_name",
                table: "domains",
                columns: new[] { "app_id", "service_name" },
                unique: true);
        }
    }
}
