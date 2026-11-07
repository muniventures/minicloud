using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Minicloud.Common.Migrations
{
    /// <inheritdoc />
    public partial class RegistryDeletionCleanup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "registry_garbage_candidates",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    repository = table.Column<string>(type: "text", nullable: false),
                    manifest_digest = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_registry_garbage_candidates", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_registry_garbage_candidates_repository_manifest_digest",
                table: "registry_garbage_candidates",
                columns: new[] { "repository", "manifest_digest" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "registry_garbage_candidates");
        }
    }
}
