using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Minicloud.Common.Migrations
{
    /// <inheritdoc />
    public partial class ApplicationEnvironments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "environment_key",
                table: "apps",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "environment_name",
                table: "apps",
                type: "character varying(63)",
                maxLength: 63,
                nullable: true);

            migrationBuilder.Sql("UPDATE apps SET environment_name = 'production', environment_key = id || ':production' WHERE parent_app_id IS NULL;");

            migrationBuilder.CreateIndex(
                name: "IX_apps_environment_key",
                table: "apps",
                column: "environment_key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_apps_environment_key",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "environment_key",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "environment_name",
                table: "apps");
        }
    }
}
