using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Minicloud.Common.Migrations
{
    /// <inheritdoc />
    public partial class IdentityAndBrowserSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_users_firebase_uid",
                table: "users");

            migrationBuilder.DropColumn(
                name: "firebase_uid",
                table: "users");

            migrationBuilder.AddColumn<string>(
                name: "kratos_identity_id",
                table: "users",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "bootstrap_records",
                columns: table => new
                {
                    installation_id = table.Column<string>(type: "text", nullable: false),
                    operation_id = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    setup_token_hash = table.Column<string>(type: "text", nullable: true),
                    setup_token_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    kratos_identity_id = table.Column<string>(type: "text", nullable: true),
                    user_id = table.Column<string>(type: "text", nullable: true),
                    organization_id = table.Column<string>(type: "text", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bootstrap_records", x => x.installation_id);
                });

            migrationBuilder.CreateTable(
                name: "browser_sessions",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    kratos_identity_id = table.Column<string>(type: "text", nullable: false),
                    kratos_session_id = table.Column<string>(type: "text", nullable: false),
                    token_hash = table.Column<string>(type: "text", nullable: false),
                    csrf_token_hash = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    idle_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_browser_sessions", x => x.id);
                    table.ForeignKey(
                        name: "FK_browser_sessions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_users_kratos_identity_id",
                table: "users",
                column: "kratos_identity_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_bootstrap_records_operation_id",
                table: "bootstrap_records",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_browser_sessions_kratos_session_id",
                table: "browser_sessions",
                column: "kratos_session_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_browser_sessions_token_hash",
                table: "browser_sessions",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_browser_sessions_user_id",
                table: "browser_sessions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_browser_sessions_user_id_revoked_at_idle_expires_at",
                table: "browser_sessions",
                columns: new[] { "user_id", "revoked_at", "idle_expires_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bootstrap_records");

            migrationBuilder.DropTable(
                name: "browser_sessions");

            migrationBuilder.DropIndex(
                name: "IX_users_kratos_identity_id",
                table: "users");

            migrationBuilder.DropColumn(
                name: "kratos_identity_id",
                table: "users");

            migrationBuilder.AddColumn<string>(
                name: "firebase_uid",
                table: "users",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_users_firebase_uid",
                table: "users",
                column: "firebase_uid",
                unique: true);
        }
    }
}
