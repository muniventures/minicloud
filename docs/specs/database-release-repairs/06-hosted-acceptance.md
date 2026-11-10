# Complete database hosted acceptance

Status: specification only; the hosted cases below remain unverified.
Date: 2026-11-10. Order: final integration lane; prepare cases in parallel with repairs.

## Outcome and scope

Complete the outstanding backup, recovery, connectivity and explicit-migration checks for the existing database release. This closes verification gaps; it does not add capabilities or reopen the full original implementation project.

Exclude clusters, replicas, PITR, failover, SQLite relocation/conversion, new providers, a new validation framework and extra paid hosts for parallel execution. Keep the original [database contract](../database-management-and-backups.md#acceptance-criteria).

## Current implementation and ownership

| Existing source | Relevant behavior |
| --- | --- |
| [DatabaseBackupProcessor.cs:71](../../../modules/worker/Databases/DatabaseBackupProcessor.cs#L71) and [:220](../../../modules/worker/Databases/DatabaseBackupProcessor.cs#L220) | Capture and restore already exist; SQLite recovery writes a separate app-local file. |
| [DatabaseBackupDownloadsController.cs:9](../../../modules/api/Controllers/DatabaseBackupDownloadsController.cs#L9), [DatabaseConnectionsController.cs:19](../../../modules/api/Controllers/DatabaseConnectionsController.cs#L19) | Authenticated artifact download and dedicated recovery-material export exist. |
| [DatabaseNetworkProcessor.cs:13](../../../modules/worker/Databases/DatabaseNetworkProcessor.cs#L13) and [:40](../../../modules/worker/Databases/DatabaseNetworkProcessor.cs#L40) | Managed bindings and access-mode reconciliation already exist. |
| [DatabaseMigrationProcessor.cs:12](../../../modules/worker/Databases/DatabaseMigrationProcessor.cs#L12) | Explicit migration fences the source, restores a target, rolls out bindings and retains the source. |
| [DatabasesController.cs:69](../../../modules/api/Controllers/DatabasesController.cs#L69) | Operation responses omit internal result JSON and target identities. |

One acceptance owner reuses existing scripts, probe fixtures and supported APIs; historical private helpers are optional aids, not prerequisites. No new runner or framework is a deliverable. Keep provider configuration, credentials and operator execution in private operations/ignored artifacts; put reusable credential-free assertions beside existing tests. Reuse the existing crypto/restore functions in [restore-check.py:90](../../../scripts/database-platform/restore-check.py#L90) and [restore.py:18](../../../scripts/database-platform/restore.py#L18). The PostgreSQL verifier returns a table count, not fixture rows, and its CLI restore path does not handle SQLite. Use bounded fixture-row assertions and the existing engine-specific restore path; do not expand the CLI into a new verification feature.

The runner must resolve PostgreSQL restore/migration destinations through organization-scoped inventory using the saved unique target name and server ID, then require exactly one ready match. Never assume omitted operation fields exist. Derive the SQLite recovery filename from its recorded restore-operation ID.

## Dependencies and resource budget

Prepare synthetic cases, polling and checkpoint handling alongside specs 01–05. Start hosted mutations only after relevant diagnostics, agent-compatibility, deletion and lifecycle/readiness repairs pass their focused checks. Use the canonical CLI delivery from spec 05 for its client smoke; API-based runner preparation can proceed independently.

Reconcile the previously blocked Vultr fixture through normal APIs before allocating fresh paid resources. Record whether it is removed or safely adopted with current ownership; do not create an untracked replacement.

Reuse at most seven simultaneous VPS roles from the original matrix:

| Role | Provider | Purpose |
| --- | --- | --- |
| App PostgreSQL | Hetzner | Existing app enrollment, native-network client and migration source. |
| App SQLite | DigitalOcean | Existing app enrollment and reusable PostgreSQL probe consumer. |
| Shared 1 | Hetzner | Two isolated source databases: shared-a and shared-b. |
| Shared 2 | DigitalOcean | Existing destination for three PostgreSQL restores and migration. |
| Dedicated 1 | Hetzner | Dedicated-a source. |
| Dedicated 2 | Vultr | Dedicated-b source and deletion regression. |
| Repository | Hetzner | Independent backup host with supported registered persistent volume. |

Use one supported Hetzner native private network. Enrollment preserves both apps' original rows and local configuration. Reuse the SQLite app's probe service instead of provisioning another consumer. Names are logical roles, never existing resource identifiers.

## Ordered acceptance cases

Seed original sources once with an identifiable synthetic row and record its expected value. Never seed a restored or migrated destination before reading its recovered row.

| Order | Case | Required observation |
| --- | --- | --- |
| 1 | Managed connectivity for shared-a, shared-b, dedicated-a and dedicated-b | Bind selected consumer service, wait for applied state, read/write the original source, unbind and wait for revocation. Include a cross-provider path and denied unbound access. |
| 2 | Public access for shared-a, dedicated-a and dedicated-b | Enable explicit TLS/IP allowlist; allowed client reads the row, excluded client fails; valid shared-b credentials fail through shared-a's public path. Close access, await reconciliation and verify denial. |
| 3 | Native private access for shared-a | Use the compatible Hetzner account/region and app PostgreSQL client; authorized access succeeds, shared-b remains isolated, revoked access fails. Unsupported configurations reject without public fallback. Finish before migration. |
| 4 | Capture/download all six sources | App PostgreSQL, app SQLite, shared-a, shared-b, dedicated-a and dedicated-b each produce a complete encrypted artifact and authenticated manifest on the independent repository. Export recovery material through its dedicated boundary. |
| 5 | Offline recovery of all six downloads | Reused restore functions restore each artifact into an isolated engine-compatible target; the runner reads the expected schema/row and confirms wrong keys, manifest tampering and corrupted ciphertext are rejected. Preserve source identity in the evidence. |
| 6 | Four hosted restore-as-new cases | Restore app PostgreSQL, shared-a and dedicated-b into separately named databases on Shared 2; restore app SQLite into its own app-local recovery file. Read the exact recovered row before seeding. Sources and shared-b remain unchanged. |
| 7 | Explicit app PostgreSQL migration | Migrate to Shared 2; observe writer fencing, correct target row, applied service bindings and a fresh successful rollout. Confirm policy parity including indefinite-retention choice, source retention and original app-host preservation. |
| 8 | Canonical client smoke, after spec 05 | Discover/authenticate/list through the delivered CLI; deploy to the existing explicit fixture environment and verify its binding is preserved. Reuse the post-migration deployment where possible. Do this before deleting the fixtures. |
| 9 | Teardown | Exercise normal deletion after operations finish; verify control-plane and provider-resource absence and scoped-key revocation. |

For PostgreSQL restore verification, bind the existing probe consumer to each restored target, wait for applied state and read its fixture row; unbind and wait for revocation before the next target. For SQLite, read `data/sqlite/recovery-<restoreOperationId>.db` through the existing synthetic probe while proving the original app database remains unchanged. Do not expose a generic SQL or shell endpoint.

Migration passes only when the application reads the recovered row after cutover and a subsequent normal/scoped deployment preserves that binding. Verify the retained source's fence and rollback information without reopening writes or deleting it as an implicit migration step. An operation's completed status alone is insufficient proof.

## Parallel work and mutation safety

- Case preparation and local verifier/test updates may proceed independently of product fixes. The acceptance owner owns these assertions and existing helper changes; fix owners retain their product files.
- One coordinator owns live fixture creation, mutation scheduling and teardown. Serialize cases sharing a consumer, source database, server access policy or operation lock; backups must not race source seeding, binding changes or migration.
- Run independent offline checks in parallel. Do not allocate extra VPSs to parallelize hosted cases or let agents concurrently rewrite a shared consumer's database connection.
- Save per-run ownership, intended request, unique resource name, operation ID and last observed state in ignored `.local/artifacts/` before/after each mutation. Redact logs; keep scoped secrets in protected temporary storage outside reports.
- After an ambiguous response, reconcile saved intent against scoped inventory and operation history before retrying. Never blindly repeat resource creation, restore, migration or deletion.

## Validation and completion

Extend existing tests only where the runner exposes a missing assertion: [test_restore_check.py:88](../../../scripts/database-platform/test_restore_check.py#L88), [test_runtime.py:130](../../../scripts/database-platform/test_runtime.py#L130), [test_access.py:4](../../../scripts/database-platform/test_access.py#L4), [test_private_network.py:15](../../../scripts/database-platform/test_private_network.py#L15), [test_migration_fence.py:9](../../../scripts/database-platform/test_migration_fence.py#L9) and [DatabaseOperationPostgresTests.cs:16](../../../modules/tests/Minicloud.Worker.Tests/Databases/DatabaseOperationPostgresTests.cs#L16). These are local checks, not provider qualification.

Wait for owned asynchronous work to become terminal before teardown or credential revocation. Disable owned access policies, revoke bindings, purge only disposable test backups through normal authorized APIs, then delete managed databases, servers, apps, repository resources and the owned network in dependency order. Handle retained-backup guards explicitly. Confirm provider VPS, volume and network absence independently of control-plane deleted status. Never delete baseline apps, shared resources or resources without proven run ownership.

Revoke all run-scoped/bootstrap keys and remove secret files after cleanup. A partial fixture ledger containing apps but no saved databases must still permit app cleanup; rely on the spec 01 helper repair rather than abandoning resources.

Complete when every matrix case has a recorded pass, restored data was read before destination seeding, no unowned data changed, all disposable resources are absent and run credentials are revoked. Record any blocked case and remaining paid resource explicitly; do not mark the matrix complete or silently grow its scope. Store execution evidence under ignored artifacts, with a concise maintained result summary containing no private identifiers or secrets. No hosted execution is performed by writing this specification.
