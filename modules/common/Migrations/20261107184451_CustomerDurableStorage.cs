using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Minicloud.Common.Migrations
{
    /// <inheritdoc />
    public partial class CustomerDurableStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "storage_json",
                table: "deployment_services",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "app_storage_volumes",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    app_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    region = table.Column<string>(type: "text", nullable: false),
                    provider_volume_id = table.Column<string>(type: "text", nullable: true),
                    creation_attempted = table.Column<bool>(type: "boolean", nullable: false),
                    size_gb = table.Column<int>(type: "integer", nullable: false),
                    uid = table.Column<int>(type: "integer", nullable: false),
                    gid = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_storage_volumes", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_app_storage_volumes_app_id_name",
                table: "app_storage_volumes",
                columns: new[] { "app_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_app_storage_volumes_provider_volume_id",
                table: "app_storage_volumes",
                column: "provider_volume_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_storage_volumes");

            migrationBuilder.DropColumn(
                name: "storage_json",
                table: "deployment_services");
        }
    }
}
