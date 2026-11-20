using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Minicloud.Common.Migrations
{
    /// <inheritdoc />
    public partial class DevelopmentWorkspaceVmResourceHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "development_workspace_vm_resource_samples",
                columns: table => new
                {
                    vm_id = table.Column<string>(type: "text", nullable: false),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    minute = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    workspace_id = table.Column<string>(type: "text", nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cpu_utilization_percent = table.Column<double>(type: "double precision", nullable: true),
                    memory_total_bytes = table.Column<long>(type: "bigint", nullable: true),
                    memory_available_bytes = table.Column<long>(type: "bigint", nullable: true),
                    workspace_disk_total_bytes = table.Column<long>(type: "bigint", nullable: true),
                    workspace_disk_available_bytes = table.Column<long>(type: "bigint", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_development_workspace_vm_resource_samples", x => new { x.vm_id, x.generation, x.minute });
                    table.ForeignKey(
                        name: "FK_development_workspace_vm_resource_samples_development_works~",
                        columns: x => new { x.workspace_id, x.vm_id },
                        principalTable: "development_workspace_vms",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_vm_resource_samples_expires_at",
                table: "development_workspace_vm_resource_samples",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_vm_resource_samples_workspace_id_vm_id",
                table: "development_workspace_vm_resource_samples",
                columns: new[] { "workspace_id", "vm_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "development_workspace_vm_resource_samples");
        }
    }
}
