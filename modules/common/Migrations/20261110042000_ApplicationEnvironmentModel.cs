using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Minicloud.Common.Migrations
{
    /// <inheritdoc />
    public partial class ApplicationEnvironmentModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "application_id",
                table: "apps",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "environment_kind",
                table: "apps",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "persistent");

            migrationBuilder.AddColumn<string>(
                name: "normalized_environment_name",
                table: "apps",
                type: "character varying(63)",
                maxLength: 63,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_branch",
                table: "apps",
                type: "character varying(63)",
                maxLength: 63,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_environment_id",
                table: "apps",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "runtime_parent_slug",
                table: "apps",
                type: "character varying(63)",
                maxLength: 63,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "applications",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    slug = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    normalized_slug = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    default_environment_id = table.Column<string>(type: "text", nullable: true),
                    legacy_root_app_id = table.Column<string>(type: "text", nullable: true),
                    deletion_status = table.Column<string>(type: "text", nullable: true),
                    deletion_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_applications", x => x.id);
                    table.UniqueConstraint("AK_applications_organization_id_id", x => new { x.organization_id, x.id });
                    table.ForeignKey(
                        name: "FK_applications_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_applications_legacy_root_app_id",
                table: "applications",
                column: "legacy_root_app_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_applications_organization_id_normalized_slug",
                table: "applications",
                columns: new[] { "organization_id", "normalized_slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_applications_organization_id_slug",
                table: "applications",
                columns: new[] { "organization_id", "slug" },
                unique: true);

            migrationBuilder.Sql("""
                UPDATE apps c SET runtime_parent_slug = p.slug
                FROM apps p WHERE c.parent_app_id = p.id AND c.branch_name IS NOT NULL;

                UPDATE apps
                SET environment_kind = 'internal',
                    application_id = NULL,
                    normalized_environment_name = NULL
                WHERE slug LIKE 'dbrepo-%'
                   OR EXISTS (SELECT 1 FROM managed_database_servers s WHERE s.runtime_app_id = apps.id);

                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM apps c
                        LEFT JOIN apps p ON p.id = c.parent_app_id
                        WHERE c.parent_app_id IS NOT NULL
                          AND c.environment_kind <> 'internal'
                          AND (p.id IS NULL OR p.parent_app_id IS NOT NULL OR p.environment_kind = 'internal')
                    ) THEN
                        RAISE EXCEPTION 'migration_preflight_failed: orphaned_parent';
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM apps c
                        JOIN apps p ON p.id = c.parent_app_id
                        WHERE c.parent_app_id IS NOT NULL
                          AND c.environment_kind <> 'internal'
                          AND c.organization_id <> p.organization_id
                    ) THEN
                        RAISE EXCEPTION 'migration_preflight_failed: cross_organization_parent';
                    END IF;

                    IF EXISTS (
                        SELECT organization_id, lower(slug)
                        FROM apps
                        WHERE parent_app_id IS NULL
                          AND environment_kind <> 'internal'
                        GROUP BY organization_id, lower(slug)
                        HAVING count(*) > 1
                    ) THEN
                        RAISE EXCEPTION 'migration_preflight_failed: duplicate_application_slug';
                    END IF;

                    IF EXISTS (
                        SELECT 1 FROM apps
                        WHERE environment_kind <> 'internal'
                          AND COALESCE(NULLIF(btrim(environment_name), ''),
                              CASE WHEN branch_name IS NOT NULL THEN btrim(branch_name)
                                   WHEN parent_app_id IS NULL THEN 'production' ELSE slug END)
                              !~ '^[A-Za-z0-9][A-Za-z0-9_-]{0,62}$'
                    ) THEN
                        RAISE EXCEPTION 'migration_preflight_failed: invalid_environment_name; provide an explicit environment mapping before retrying';
                    END IF;

                    IF EXISTS (
                        SELECT parent_app_id,
                               lower(COALESCE(NULLIF(btrim(environment_name), ''), btrim(branch_name)))
                        FROM apps
                        WHERE branch_name IS NOT NULL AND environment_kind <> 'internal'
                        GROUP BY parent_app_id, lower(COALESCE(NULLIF(btrim(environment_name), ''), btrim(branch_name)))
                        HAVING count(*) > 1
                    ) THEN
                        RAISE EXCEPTION 'migration_preflight_failed: branch_environment_name_conflict';
                    END IF;

                    IF EXISTS (
                        SELECT COALESCE(parent_app_id, id) AS root_id,
                               lower(COALESCE(NULLIF(btrim(environment_name), ''), CASE WHEN parent_app_id IS NULL THEN 'production' ELSE slug END)) AS norm_name
                        FROM apps
                        WHERE branch_name IS NULL
                          AND environment_kind <> 'internal'
                        GROUP BY COALESCE(parent_app_id, id),
                                 lower(COALESCE(NULLIF(btrim(environment_name), ''), CASE WHEN parent_app_id IS NULL THEN 'production' ELSE slug END))
                        HAVING count(*) > 1
                    ) THEN
                        RAISE EXCEPTION 'migration_preflight_failed: duplicate_environment_name';
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM apps b
                        JOIN apps e ON COALESCE(e.parent_app_id, e.id) = b.parent_app_id
                        WHERE b.parent_app_id IS NOT NULL
                          AND b.branch_name IS NOT NULL
                          AND b.environment_kind <> 'internal'
                          AND e.branch_name IS NULL
                          AND e.environment_kind <> 'internal'
                          AND lower(COALESCE(NULLIF(btrim(b.environment_name), ''), btrim(b.branch_name))) =
                              lower(COALESCE(NULLIF(btrim(e.environment_name), ''), CASE WHEN e.parent_app_id IS NULL THEN 'production' ELSE e.slug END))
                    ) THEN
                        RAISE EXCEPTION 'migration_preflight_failed: branch_environment_name_conflict';
                    END IF;
                END $$;

                INSERT INTO applications (
                    id, organization_id, name, slug, normalized_slug,
                    default_environment_id, legacy_root_app_id,
                    deletion_status, deletion_error, created_at, updated_at
                )
                SELECT
                    CASE WHEN left(r.id, 4) = 'app_' THEN 'appl_' || substr(r.id, 5) ELSE 'appl_' || r.id END,
                    r.organization_id,
                    r.name,
                    r.slug,
                    lower(r.slug),
                    r.id,
                    r.id,
                    r.deletion_status,
                    r.deletion_error,
                    r.created_at,
                    r.updated_at
                FROM apps r
                WHERE r.parent_app_id IS NULL
                  AND r.environment_kind <> 'internal'
                ON CONFLICT (legacy_root_app_id) DO NOTHING;

                UPDATE apps r
                SET application_id = a.id,
                    environment_kind = 'persistent',
                    environment_name = COALESCE(NULLIF(btrim(r.environment_name), ''), 'production'),
                    normalized_environment_name = lower(COALESCE(NULLIF(btrim(r.environment_name), ''), 'production')),
                    environment_key = r.id || ':' || lower(COALESCE(NULLIF(btrim(r.environment_name), ''), 'production'))
                FROM applications a
                WHERE a.legacy_root_app_id = r.id
                  AND r.parent_app_id IS NULL
                  AND r.environment_kind <> 'internal';

                UPDATE apps c
                SET application_id = a.id,
                    environment_kind = 'persistent',
                    environment_name = COALESCE(NULLIF(btrim(c.environment_name), ''), c.slug),
                    normalized_environment_name = lower(COALESCE(NULLIF(btrim(c.environment_name), ''), c.slug)),
                    environment_key = c.parent_app_id || ':' || lower(COALESCE(NULLIF(btrim(c.environment_name), ''), c.slug))
                FROM applications a
                WHERE a.legacy_root_app_id = c.parent_app_id
                  AND c.parent_app_id IS NOT NULL
                  AND c.branch_name IS NULL
                  AND c.environment_kind <> 'internal';

                UPDATE apps c
                SET application_id = a.id,
                    environment_kind = 'preview',
                    source_branch = COALESCE(c.source_branch, c.branch_name),
                    source_environment_id = COALESCE(c.source_environment_id, c.parent_app_id),
                    environment_name = COALESCE(NULLIF(btrim(c.environment_name), ''), btrim(c.branch_name)),
                    normalized_environment_name = lower(COALESCE(NULLIF(btrim(c.environment_name), ''), btrim(c.branch_name))),
                    environment_key = c.parent_app_id || ':' || lower(COALESCE(NULLIF(btrim(c.environment_name), ''), btrim(c.branch_name)))
                FROM applications a
                WHERE a.legacy_root_app_id = c.parent_app_id
                  AND c.parent_app_id IS NOT NULL
                  AND c.branch_name IS NOT NULL
                  AND c.environment_kind <> 'internal';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_apps_application_id_normalized_environment_name",
                table: "apps",
                columns: new[] { "application_id", "normalized_environment_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_apps_organization_id_application_id",
                table: "apps",
                columns: new[] { "organization_id", "application_id" });

            migrationBuilder.AddForeignKey(
                name: "FK_apps_applications_organization_id_application_id",
                table: "apps",
                columns: new[] { "organization_id", "application_id" },
                principalTable: "applications",
                principalColumns: new[] { "organization_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_apps_applications_organization_id_application_id",
                table: "apps");

            migrationBuilder.DropTable(
                name: "applications");

            migrationBuilder.DropIndex(
                name: "IX_apps_application_id_normalized_environment_name",
                table: "apps");

            migrationBuilder.DropIndex(
                name: "IX_apps_organization_id_application_id",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "runtime_parent_slug",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "application_id",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "environment_kind",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "normalized_environment_name",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "source_branch",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "source_environment_id",
                table: "apps");
        }
    }
}
