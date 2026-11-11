using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Minicloud.Common.Migrations
{
    /// <inheritdoc />
    public partial class DeploymentNumbers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Block concurrent deployment inserts so the backfill and counter seed see one stable row set.
            migrationBuilder.Sql("LOCK TABLE deployments IN SHARE ROW EXCLUSIVE MODE;");

            migrationBuilder.AddColumn<string>(
                name: "number",
                table: "deployments",
                type: "character varying(28)",
                maxLength: 28,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "deployment_number_counters",
                columns: table => new
                {
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    utc_date = table.Column<DateOnly>(type: "date", nullable: false),
                    last_value = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deployment_number_counters", x => new { x.organization_id, x.utc_date });
                    table.CheckConstraint("ck_deployment_number_counters_positive", "last_value >= 1");
                });

            // Deterministic backfill: partition by organization and UTC creation date, order by
            // created_at then the opaque ID (byte order), and seed each counter to its partition maximum.
            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT id,
                           (created_at AT TIME ZONE 'UTC')::date AS utc_date,
                           ROW_NUMBER() OVER (
                               PARTITION BY organization_id, (created_at AT TIME ZONE 'UTC')::date
                               ORDER BY created_at, id COLLATE "C") AS counter
                    FROM deployments)
                UPDATE deployments AS d
                SET number = to_char(r.utc_date, 'YYYYMMDD') || '.' || r.counter::text
                FROM ranked AS r
                WHERE d.id = r.id;

                INSERT INTO deployment_number_counters (organization_id, utc_date, last_value)
                SELECT organization_id, (created_at AT TIME ZONE 'UTC')::date, COUNT(*)
                FROM deployments
                GROUP BY organization_id, (created_at AT TIME ZONE 'UTC')::date;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "number",
                table: "deployments",
                type: "character varying(28)",
                maxLength: 28,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(28)",
                oldMaxLength: 28,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_deployments_organization_id_number",
                table: "deployments",
                columns: new[] { "organization_id", "number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "deployment_number_counters");

            migrationBuilder.DropIndex(
                name: "IX_deployments_organization_id_number",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "number",
                table: "deployments");
        }
    }
}
