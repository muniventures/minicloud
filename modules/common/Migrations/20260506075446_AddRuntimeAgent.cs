using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Minicloud.Common.Migrations
{
    /// <inheritdoc />
    public partial class AddRuntimeAgent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "runtime_log_records",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    app_id = table.Column<string>(type: "text", nullable: false),
                    runtime_server_id = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    service = table.Column<string>(type: "text", nullable: true),
                    stream = table.Column<string>(type: "text", nullable: false),
                    cursor = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runtime_log_records", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "runtime_servers",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    app_id = table.Column<string>(type: "text", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false),
                    provider_server_id = table.Column<string>(type: "text", nullable: true),
                    hostname = table.Column<string>(type: "text", nullable: true),
                    agent_version = table.Column<string>(type: "text", nullable: true),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runtime_servers", x => x.id);
                    table.ForeignKey(
                        name: "FK_runtime_servers_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_runtime_servers_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "runtime_status_snapshots",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    app_id = table.Column<string>(type: "text", nullable: false),
                    runtime_server_id = table.Column<string>(type: "text", nullable: false),
                    agent_version = table.Column<string>(type: "text", nullable: true),
                    status_json = table.Column<string>(type: "text", nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runtime_status_snapshots", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "runtime_tokens",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    runtime_server_id = table.Column<string>(type: "text", nullable: false),
                    token_prefix = table.Column<string>(type: "text", nullable: false),
                    token_hash = table.Column<string>(type: "text", nullable: false),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runtime_tokens", x => x.id);
                    table.ForeignKey(
                        name: "FK_runtime_tokens_runtime_servers_runtime_server_id",
                        column: x => x.runtime_server_id,
                        principalTable: "runtime_servers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_runtime_log_records_app_id_observed_at",
                table: "runtime_log_records",
                columns: new[] { "app_id", "observed_at" });

            migrationBuilder.CreateIndex(
                name: "IX_runtime_log_records_runtime_server_id_source_cursor_observe~",
                table: "runtime_log_records",
                columns: new[] { "runtime_server_id", "source", "cursor", "observed_at" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_runtime_servers_app_id",
                table: "runtime_servers",
                column: "app_id");

            migrationBuilder.CreateIndex(
                name: "IX_runtime_servers_organization_id",
                table: "runtime_servers",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_runtime_servers_provider_provider_server_id",
                table: "runtime_servers",
                columns: new[] { "provider", "provider_server_id" });

            migrationBuilder.CreateIndex(
                name: "IX_runtime_status_snapshots_app_id_received_at",
                table: "runtime_status_snapshots",
                columns: new[] { "app_id", "received_at" });

            migrationBuilder.CreateIndex(
                name: "IX_runtime_tokens_runtime_server_id_revoked_at",
                table: "runtime_tokens",
                columns: new[] { "runtime_server_id", "revoked_at" });

            migrationBuilder.CreateIndex(
                name: "IX_runtime_tokens_token_prefix",
                table: "runtime_tokens",
                column: "token_prefix");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "runtime_log_records");

            migrationBuilder.DropTable(
                name: "runtime_status_snapshots");

            migrationBuilder.DropTable(
                name: "runtime_tokens");

            migrationBuilder.DropTable(
                name: "runtime_servers");
        }
    }
}
