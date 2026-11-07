using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Minicloud.Common.Data;

#nullable disable

namespace Minicloud.Common.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(MinicloudDbContext))]
    [Migration("20260914143000_AddDomainIsPrimary")]
    public partial class AddDomainIsPrimary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_primary",
                table: "domains",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_primary",
                table: "domains");
        }
    }
}
