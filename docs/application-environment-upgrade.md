# Application/environment upgrade

The application/environment migration separates the product identity in `applications`
from its environment runtimes in `apps`. The latter table and its IDs remain in place
so existing server, deployment, domain, storage, and secret-store references survive.
`MinicloudApp` is the compatibility name of the runtime environment entity.

This is a maintenance-window upgrade. Local migration tests do not qualify a hosted
installation or authorize running this procedure against production.

## Before migration

1. Back up PostgreSQL and verify restoration on a disposable installation. Keep the
   previous API/worker binaries and record the current migration revision.
2. Stop admission of mutations and drain active deployment, preparation, database,
   asset, and deletion jobs. Stop old API/worker writers before applying the migration.
   Do not run old and new workers against the upgraded database concurrently.
3. Inventory runtime IDs, parent relationships, environment and branch names, provider
   account/server/volume bindings, domains/TLS state, and secret-store references.
   Record counts and identifiers without exporting credential values.
4. Rehearse migration `20261110042000_ApplicationEnvironmentModel` on the restored
   backup using the installation's existing migration workflow. API startup can apply
   EF migrations when `Database:RunMigrationsOnStartup` is enabled; do not expose the
   upgraded API to users until reconciliation completes.

The migration's transactional preflight rejects orphaned/nested parents,
cross-organization relationships, duplicate case-insensitive application slugs,
invalid environment names, and duplicate/colliding persistent or preview names.
The error includes `migration_preflight_failed` and a conflict code. A failed
transaction must leave the migration unapplied; investigate rather than bypassing
its constraints.

Resolve conflicts on the maintenance copy first. A preview-name conflict requires an
explicit reviewed mapping: set the legacy row's `environment_name` to the chosen
valid unique name before retrying. Keep `branch_name`, runtime ID, and slug unchanged.
Do not automatically rename, merge, or delete customer records to force migration.

## Reconciliation and release

Each customer root maps to one application through its persisted
`legacy_root_app_id`. Root and child runtime IDs become environment IDs. Persistent
names retain their casing, null root names become `production`, and branches become
previews with source provenance. Managed database runtime records and reserved
`dbrepo-` repository records remain internal. A customer slug beginning with `db-`
alone does not make the app internal. Independent apps stay independent.

Before reopening writes, compare the recorded inventory with the upgraded database:

- Customer applications, environment membership/kind, names, and default environment.
- Unchanged runtime IDs/slugs, provider account/server IDs, and retained volume IDs.
- Unchanged generated/custom hostnames, TLS state, secret-store paths/versions,
  deployment history, and expected absence of active jobs.
- `/api/apps` returns applications with scoped environments; operational environment reads use `/api/environments/{environmentId}`.
- CLI configs contain canonical application IDs. No old-client or root-ID aliases are served.
- Selecting the dashboard default must not select a CLI deployment target.

Start API/workers, validate those checks with writes still restricted,
then publish matching dashboard/CLI clients through the normal release workflow.
Reopen writes only after reconciliation. Environment deletion may update legacy
parent links, but explicit application membership and saved preview routing identity
remain authoritative.

Before reopening writes, restore the verified backup and previous binaries if the
upgrade fails. After reopening writes, use a reviewed forward repair or a separately
tested reverse migration; do not restore a stale backup over new customer operations.

The persisted `legacy_root_app_id` field is migration bookkeeping only. It does not resolve API or CLI aliases. Update saved configuration to canonical application IDs before running the new clients.
