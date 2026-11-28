using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Selfrun.Common.Migrations
{
    /// <inheritdoc />
    public partial class EnvironmentPowerWakeQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_environment_power_operations_active",
                table: "environment_power_operations");

            migrationBuilder.CreateIndex(
                name: "ux_environment_power_operations_active_kind",
                table: "environment_power_operations",
                columns: new[] { "environment_id", "kind" },
                unique: true,
                filter: "status NOT IN ('completed', 'failed', 'cancelled')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_environment_power_operations_active_kind",
                table: "environment_power_operations");

            migrationBuilder.CreateIndex(
                name: "ux_environment_power_operations_active",
                table: "environment_power_operations",
                column: "environment_id",
                unique: true,
                filter: "status NOT IN ('completed', 'failed', 'cancelled')");
        }
    }
}
