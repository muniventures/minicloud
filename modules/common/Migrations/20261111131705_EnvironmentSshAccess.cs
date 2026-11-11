using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Minicloud.Common.Migrations
{
    /// <inheritdoc />
    public partial class EnvironmentSshAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ssh_credential_generation",
                table: "deployments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ssh_fingerprint",
                table: "deployments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ssh_key_id",
                table: "deployments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ssh_key_name",
                table: "deployments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ssh_key_type",
                table: "deployments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ssh_provider_account_id",
                table: "deployments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ssh_provider_references_json",
                table: "deployments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ssh_public_key",
                table: "deployments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ssh_registration_state",
                table: "deployments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ssh_selecting_actor",
                table: "deployments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ssh_selection_mode",
                table: "deployments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ssh_selection_revision",
                table: "deployments",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ssh_snapshot_taken_at",
                table: "deployments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ssh_access_mode",
                table: "apps",
                type: "text",
                nullable: false,
                defaultValue: "none");

            migrationBuilder.AddColumn<string>(
                name: "ssh_fingerprint",
                table: "apps",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ssh_key_id",
                table: "apps",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ssh_key_name",
                table: "apps",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ssh_key_type",
                table: "apps",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ssh_provider_account_id",
                table: "apps",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ssh_public_key",
                table: "apps",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ssh_registration_state",
                table: "apps",
                type: "text",
                nullable: false,
                defaultValue: "not_registered");

            migrationBuilder.AddColumn<long>(
                name: "ssh_revision",
                table: "apps",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ssh_selected_at",
                table: "apps",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ssh_selected_by",
                table: "apps",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "provider_ssh_keys",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    provider_account_id = table.Column<string>(type: "text", nullable: false),
                    provider_key_id = table.Column<string>(type: "text", nullable: true),
                    name = table.Column<string>(type: "text", nullable: false),
                    public_key = table.Column<string>(type: "text", nullable: false),
                    fingerprint = table.Column<string>(type: "text", nullable: false),
                    key_type = table.Column<string>(type: "text", nullable: false),
                    registration_state = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    registered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_ssh_keys", x => x.id);
                    table.UniqueConstraint("AK_provider_ssh_keys_organization_id_provider_account_id_id", x => new { x.organization_id, x.provider_account_id, x.id });
                    table.ForeignKey(
                        name: "FK_provider_ssh_keys_provider_accounts_organization_id_provide~",
                        columns: x => new { x.organization_id, x.provider_account_id },
                        principalTable: "provider_accounts",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_apps_organization_id_ssh_provider_account_id_ssh_key_id",
                table: "apps",
                columns: new[] { "organization_id", "ssh_provider_account_id", "ssh_key_id" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_ssh_keys_organization_id_provider_account_id_finge~",
                table: "provider_ssh_keys",
                columns: new[] { "organization_id", "provider_account_id", "fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_ssh_keys_organization_id_provider_account_id_provi~",
                table: "provider_ssh_keys",
                columns: new[] { "organization_id", "provider_account_id", "provider_key_id" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_apps_provider_accounts_organization_id_ssh_provider_account~",
                table: "apps",
                columns: new[] { "organization_id", "ssh_provider_account_id" },
                principalTable: "provider_accounts",
                principalColumns: new[] { "organization_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_apps_provider_ssh_keys_organization_id_ssh_provider_account~",
                table: "apps",
                columns: new[] { "organization_id", "ssh_provider_account_id", "ssh_key_id" },
                principalTable: "provider_ssh_keys",
                principalColumns: new[] { "organization_id", "provider_account_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_apps_provider_accounts_organization_id_ssh_provider_account~",
                table: "apps");

            migrationBuilder.DropForeignKey(
                name: "FK_apps_provider_ssh_keys_organization_id_ssh_provider_account~",
                table: "apps");

            migrationBuilder.DropTable(
                name: "provider_ssh_keys");

            migrationBuilder.DropIndex(
                name: "IX_apps_organization_id_ssh_provider_account_id_ssh_key_id",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "ssh_credential_generation",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "ssh_fingerprint",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "ssh_key_id",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "ssh_key_name",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "ssh_key_type",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "ssh_provider_account_id",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "ssh_provider_references_json",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "ssh_public_key",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "ssh_registration_state",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "ssh_selecting_actor",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "ssh_selection_mode",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "ssh_selection_revision",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "ssh_snapshot_taken_at",
                table: "deployments");

            migrationBuilder.DropColumn(
                name: "ssh_access_mode",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "ssh_fingerprint",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "ssh_key_id",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "ssh_key_name",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "ssh_key_type",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "ssh_provider_account_id",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "ssh_public_key",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "ssh_registration_state",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "ssh_revision",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "ssh_selected_at",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "ssh_selected_by",
                table: "apps");
        }
    }
}
