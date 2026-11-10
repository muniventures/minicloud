# Repair the blocked Vultr database deletion

Status: specified; cause unconfirmed; not implemented or hosted-qualified.
Date: 2026-11-10.

## Outcome

Delete the affected server-hosted PostgreSQL database through the existing database
operation, then permit normal database-server deletion. Preserve neighboring data,
access restrictions, backup retention and operation ownership throughout the repair.

## Observed failure and source

- Staging cleanup reported `database_access_change_rolled_back` twice for one Vultr
  database. Its metadata remained `creating`; its server remained `ready` because
  normal server deletion correctly rejected a remaining database.
- Other shared/dedicated fixture deletions succeeded. This does not establish a
  Vultr-wide failure or identify a provider, firewall, TLS or Docker root cause.
- Resource identifiers and detailed diagnostics remain in private acceptance
  artifacts. Use synthetic identities in committed tests and documentation.
- `modules/worker/Databases/DatabaseOperationProcessor.cs:317` performs remote
  deletion, sets the tracked database status, then reconciles host access.
- `modules/worker/Databases/DatabaseNetworkProcessor.cs:91` builds the remaining
  host's effective access configuration and dispatches its application.
- `scripts/database-platform/access.py:149` applies access; line 206 restores prior
  configuration on failure. Line 230 raises `AccessChangeRolledBackError` after
  that rollback succeeds. This describes access rollback, not database restoration.
- `modules/worker/Databases/DatabaseOperationProcessor.cs:374` maps that category
  to the observed code; line 188 reloads persisted state after operation failure.
- `modules/api/Controllers/DatabaseServersController.cs:36` rejects server deletion
  while any database remains non-deleted or an operation is active.

## Scope

Repair only the demonstrated failing deletion/access boundary. Do not add a force
delete endpoint, general retry service, provider abstraction, automatic resource
recreation or new access mode. Do not skip access reconciliation or mark metadata
deleted solely because an earlier remote command returned success.

## Implementation order

1. Complete [diagnostics task D10](01-diagnostics-and-cleanup.md). Capture the failing access substep
   and sanitized cause before selecting a fix; the rollback code alone is insufficient.
2. Reproduce the sequence with a disposable PostgreSQL runtime: failed creation,
   retained metadata, normal delete, access reconciliation and operation completion.
   Check whether the remote database/user still exist after each failed attempt.
3. Add a failing regression at the boundary that reproduces the captured cause.
   Keep the diagnosis open if the local fixture cannot reproduce it; do not invent
   a Vultr-specific branch to make the observed error disappear.
4. Repair that boundary using the existing operation and access implementation.
   Reconcile observed remote outcome before an explicit retry; never replay an
   interrupted mutation after ownership loss.
5. Verify successful database deletion and remaining access configuration before
   finalizing metadata and backup-policy state. Preserve completed backup artifacts.
6. In coordinated hosted acceptance, retry only the owned retained fixture through
   normal APIs, then delete its server after the database is confirmed deleted.

## Acceptance criteria

- The captured failing case passes a regression and completes normal deletion;
  operation and database metadata agree with verified runtime outcome.
- A deliberate access-apply failure remains a failure. Verified rollback reports
  `database_access_change_rolled_back`; unverified rollback retains
  `database_access_reconciliation_required` and prevents false completion.
- Neighboring databases retain their exact data, credentials and allowed/denied
  connectivity. The deleted database/user cannot connect after successful deletion.
- An explicit retry handles the verified partially completed state safely; expired
  ownership or revoked authority never admits another remote mutation.
- Server deletion remains blocked until database deletion completes. No resource
  row is force-deleted, and no unrelated fixture is selected for cleanup.
- Hosted cleanup records the database operation and server's final state privately;
  local tests alone do not claim provider deletion or billing cessation.

## Ownership and parallel work

- Owner: public worker/runtime agent. Start with `DatabaseNetworkProcessor.cs`,
  `scripts/database-platform/access.py`, `scripts/database-platform/test_access.py`
  and the relevant database worker tests; edit only files implicated by reproduction.
- Reserve `DatabaseOperationProcessor.cs` and shared worker tests with the
  diagnostics and [lifecycle](04-lifecycle-readiness.md) owners. Merge D10
  first, this repair second, then lifecycle changes to that shared file.
- Independent agent compatibility, CLI delivery and private readiness work may run
  concurrently. One coordinator owns hosted fixture mutations and cleanup.
- Stop when the reproduced defect passes targeted regressions and the owned
  hosted cleanup is verified, or report the remaining specific evidence gap.
