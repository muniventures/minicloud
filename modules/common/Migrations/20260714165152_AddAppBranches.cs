using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Minicloud.Common.Migrations
{
    /// <inheritdoc />
    public partial class AddAppBranches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "branch_name",
                table: "apps",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "parent_app_id",
                table: "apps",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_apps_parent_app_id_branch_name",
                table: "apps",
                columns: new[] { "parent_app_id", "branch_name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_apps_apps_parent_app_id",
                table: "apps",
                column: "parent_app_id",
                principalTable: "apps",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_apps_apps_parent_app_id",
                table: "apps");

            migrationBuilder.DropIndex(
                name: "IX_apps_parent_app_id_branch_name",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "branch_name",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "parent_app_id",
                table: "apps");
        }
    }
}
