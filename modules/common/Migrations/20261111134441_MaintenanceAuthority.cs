using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Minicloud.Common.Migrations
{
    /// <inheritdoc />
    public partial class MaintenanceAuthority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "maintenance_activities",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    participant_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    epoch = table.Column<long>(type: "bigint", nullable: false),
                    detail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_maintenance_activities", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "maintenance_authority",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    installation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    epoch = table.Column<long>(type: "bigint", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    holder_operation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    host_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    transfer_operation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    fenced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    transferred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_maintenance_authority", x => x.id);
                    table.CheckConstraint("ck_maintenance_authority_singleton", "id = 1 AND epoch >= 1 AND state IN ('open', 'fenced')");
                });

            migrationBuilder.CreateTable(
                name: "maintenance_participants",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    host_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    epoch = table.Column<long>(type: "bigint", nullable: false),
                    registered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_maintenance_participants", x => x.id);
                });

            migrationBuilder.InsertData(
                table: "maintenance_authority",
                columns: new[] { "id", "epoch", "fenced_at", "holder_operation_id", "host_id", "installation_id", "released_at", "state", "transfer_operation_id", "transferred_at", "updated_at" },
                values: new object[] { 1, 1L, null, null, null, null, null, "open", null, null, new DateTimeOffset(new DateTime(2026, 11, 11, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });

            migrationBuilder.CreateIndex(
                name: "IX_maintenance_activities_expires_at",
                table: "maintenance_activities",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_maintenance_participants_last_seen_at",
                table: "maintenance_participants",
                column: "last_seen_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "maintenance_activities");

            migrationBuilder.DropTable(
                name: "maintenance_authority");

            migrationBuilder.DropTable(
                name: "maintenance_participants");
        }
    }
}
