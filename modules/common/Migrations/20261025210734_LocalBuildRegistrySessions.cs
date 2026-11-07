using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Minicloud.Common.Migrations
{
    /// <inheritdoc />
    public partial class LocalBuildRegistrySessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "subject_kind",
                table: "registry_sessions",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "requested_service_names_json",
                table: "deployments",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "registry_uploads",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    session_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_registry_uploads", x => x.id);
                    table.ForeignKey(
                        name: "FK_registry_uploads_registry_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "registry_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_registry_uploads_session_id",
                table: "registry_uploads",
                column: "session_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "registry_uploads");

            migrationBuilder.DropColumn(
                name: "subject_kind",
                table: "registry_sessions");

            migrationBuilder.DropColumn(
                name: "requested_service_names_json",
                table: "deployments");
        }
    }
}
