using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Minicloud.Common.Migrations
{
    /// <inheritdoc />
    public partial class AddCliLoginSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cli_login_sessions",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    organization_id = table.Column<string>(type: "TEXT", nullable: true),
                    approved_by_user_id = table.Column<string>(type: "TEXT", nullable: true),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    scopes_json = table.Column<string>(type: "TEXT", nullable: false),
                    api_key_id = table.Column<string>(type: "TEXT", nullable: true),
                    raw_api_key_token = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    approved_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    exchanged_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cli_login_sessions", x => x.id);
                    table.ForeignKey(
                        name: "FK_cli_login_sessions_api_keys_api_key_id",
                        column: x => x.api_key_id,
                        principalTable: "api_keys",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "FK_cli_login_sessions_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "FK_cli_login_sessions_users_approved_by_user_id",
                        column: x => x.approved_by_user_id,
                        principalTable: "users",
                        principalColumn: "id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_cli_login_sessions_api_key_id",
                table: "cli_login_sessions",
                column: "api_key_id");

            migrationBuilder.CreateIndex(
                name: "IX_cli_login_sessions_approved_by_user_id",
                table: "cli_login_sessions",
                column: "approved_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_cli_login_sessions_expires_at",
                table: "cli_login_sessions",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_cli_login_sessions_organization_id",
                table: "cli_login_sessions",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_cli_login_sessions_status",
                table: "cli_login_sessions",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cli_login_sessions");
        }
    }
}
