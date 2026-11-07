using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Minicloud.Common.Data;

#nullable disable

namespace Minicloud.Common.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(MinicloudDbContext))]
    [Migration("20260914121500_AddDeploymentServiceSha256")]
    public partial class AddDeploymentServiceSha256 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "sha256",
                table: "deployment_services",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "sha256",
                table: "deployment_services");
        }
    }
}
