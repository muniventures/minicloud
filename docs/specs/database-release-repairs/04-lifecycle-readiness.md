# Repair lifecycle status and maintenance readiness

Status: specified; failure causes require reproduction; not implemented.
Date: 2026-11-10.

## Outcome

Resource status must reflect terminal operation outcomes, and maintenance inventory
comparison must run against the completed, exact candidate migration. Implement
the public lifecycle and private readiness tasks independently; neither adds a new
workflow, retry API or orchestration framework.

## Observed failures

- A failed server-hosted database creation left database metadata at `creating`.
- A server deletion remained `deleting` after its operation was no longer active;
  a later normal DELETE completed it. The underlying cause is unconfirmed.
- Initial live maintenance reconciliation saw the old application schema: no
  `applications` table and unchanged application/environment fields in `apps`.
  A later strict comparison of the same candidate matched and existing recovery
  completed. This suggests a readiness/inventory timing issue but does not prove it.

## Task 04A — public lifecycle state

Source and ownership:

- `modules/worker/Databases/DatabaseOperationProcessor.cs:110` fails abandoned
  leases without replay; line 188 reloads the owned operation on failure. Line 204
  updates failed server creation, but has no corresponding database-create update.
- The same file at line 74 queues backing-app deletion, requeues while the app
  exists, and marks the server deleted only after the app is absent.
- `modules/api/Controllers/DatabaseServersController.cs:30` accepts normal DELETE
  only without remaining databases or active operations. Preserve those guards.
- Owner: public worker agent; relevant tests are
  `modules/tests/Minicloud.Worker.Tests/Databases/DatabaseOperationPostgresTests.cs`
  and `DatabaseOwnershipTests.cs` in the same directory.

Implementation and acceptance:

1. After [diagnostics task D10](01-diagnostics-and-cleanup.md), reproduce terminal create failure and
   terminal server-delete failure with their persisted operation/resource state.
2. Add regressions for known create failure, interrupted/unknown remote outcome,
   failed backing-app deletion and successful delayed backing-app deletion.
3. Apply the smallest resource-specific failure transition. A terminal failed
   create must stop presenting as active creation. Use existing supported failure
   status and operation error context when remote effects are uncertain; do not
   add a new status enum or schema as part of this repair.
4. Explain a failed server deletion through existing operation/status surfaces;
   do not leave an unexplained active-looking status with no active operation.
   Preserve retained runtime resources until their deletion is actually verified.
5. A healthy database remains healthy when its backup fails. Unrelated resources,
   newer revisions and resources outside the operation's organization stay unchanged.
6. Revoked authority cannot admit remote mutation; lost ownership cannot overwrite
   newer resource status or replay remote work.
   Normal DELETE remains the explicit recovery path when its existing guards pass;
   successful delayed deletion completes without requiring a duplicate request.

## Task 04B — private maintenance readiness

Source and ownership:

- In private hosted operations, `scripts/github-actions/minicloud-deploy/remote-deploy.sh:1903`
  runs `before-live`, Compose backend startup with `--wait`, then `reconcile`.
- `scripts/github-actions/minicloud-deploy/platform-maintenance-upgrade.py:171`
  reads inventory; line 314 requires exact equality before `reconciled`;
  line 355 rechecks saved candidate/ownership/fence during recovery.
- Public API startup awaits configured migrations in
  `modules/api/Extensions/MinicloudApplicationExtensions.cs:25` before mapping
  `/health` in `modules/api/Program.cs:19`. Verify effective candidate configuration
  and container readiness instead of assuming startup does not await migrations.
- Owner: private hosted-operations agent. Own the two deployment scripts and
  `scripts/github-actions/minicloud-deploy/test-platform-maintenance-upgrade.py`.
  Historical private `modules/` are outside the implementation boundary.

Implementation and acceptance:

1. Use retained private diagnostics and a local delayed-migration fixture to
   identify which readiness condition admitted the initial old-schema inventory.
2. Repair only the proven readiness condition at the existing deployment boundary.
   If a wait is needed, bound it and verify the running candidate, configured
   migration completion and subsequent exact inventory; avoid an arbitrary sleep.
3. A delayed candidate migration must not admit workers or public traffic early.
   Candidate failure or readiness timeout retains the maintenance fence.
4. Every real inventory mismatch still fails strict reconciliation. Do not ignore
   fields, counts, tables or rows, regenerate the expected inventory from live data,
   force `reconciled`, or restore stale backups after migration may have started.
5. Existing owner/image/fence, recovery and pre-/post-migration failure regressions
   remain passing. Successful readiness reaches exact equality before activation.

## Dependencies and stop condition

04A shared-file changes follow D10 and [Vultr deletion](03-vultr-deletion.md).
04B can investigate independently using existing private diagnostics, then implement
after reproducing the cause; it may run alongside every public repair. Coordinate
any public API change discovered by 04B with the public owner before editing.
Do not refactor to create parallelism. Each task stops after its targeted regressions
pass; hosted evidence is recorded by [hosted acceptance](06-hosted-acceptance.md).
