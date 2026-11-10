# R1 — Operation diagnostics and application-only cleanup

Status: planned; specification only. Baseline: database release `05547e5`, November 10, 2026.

## Outcome and limits

An existing operation failure provides enough sanitized evidence to reproduce its cause, and sparse deployment output is visible at meaningful phase boundaries. The operator cleanup path also works when its saved fixtures contain applications but no managed databases.

Use existing logging, events, operation records, and cleanup APIs. No new telemetry system, progress API, workflow, generic recovery framework, database schema, or retry mechanism. Do not expose secrets or expand the operations API merely to satisfy an acceptance helper.

## Evidence and entry points

- `DatabaseOperationProcessor.cs:188–218` catches operation failures and retains a generic public message; `DatabaseOperationWorker.cs:29` logs only failures escaping the processor. The original cause was unavailable from the observed operation responses.
- `DeploymentLogWriter.cs:23–56` defaults to 50 buffered lines. A transfer-start event therefore did not establish that transfer was still running. Existing flush support is at `DeploymentLogWriter.cs:65–75`.
- `DatabasesController.cs:69–74` projects operation IDs, kind, status, error fields and timestamps. It does **not** return `resultJson`, database ID, or organization ID.
- The release's private backup acceptance helper calls `status(None)` before application cleanup; its database selector rejects an empty set. Application-only production fixtures required direct normal API deletion. This is an operator-helper defect, not evidence that application deletion failed.

Product paths above are under `modules/worker/Databases/`, `modules/worker/Pipeline/Steps/`, and `modules/api/Controllers/`. Historical private helper files are optional reproduction evidence, not maintained product dependencies; do not commit their credentials, resource IDs, state, or absolute machine paths.

## Tasks and ownership

| Task | Work | Dependency / exclusive write scope |
| --- | --- | --- |
| D10 | Retain sanitized failure classification and correlation in existing operator logs. | Start first. Own `DatabaseOperationProcessor.cs`, `DatabaseOperationWorker.cs`, and their focused tests for this small patch. Hand off the processor file before R4 lifecycle edits. |
| D20 | Flush meaningful phase/error output through the existing deployment log writer. | Independent of D10. Assign to the R2 implementation lane: own `DeploymentLogWriter.cs`, its existing `SshDeployStep` integration, and focused logging tests. Do not create a competing writer for `SshDeployStep.cs`. |
| D30 | Make retained operator cleanup support an empty managed-database set. | May run alongside D10/D20; required before R6 fixture work. Own only the retained private acceptance helper and its synthetic tests, if that helper is reused. |

## Implementation requirements

1. Reproduce one generic database failure and one deployment emitting fewer than 50 lines. Identify what is already available before adding output.
2. In the existing failure path, record operation ID, kind, known stage, exception type and an existing safe error code. Preserve current ownership/lease checks, cancellation semantics, and the original error. Do not log raw exception messages, SQL, commands, payloads, connection strings, credentials, or provider response bodies by default.
3. Preserve actionable typed runtime codes such as `database_access_change_rolled_back`. Unknown failures may retain the generic public error, correlated with operator evidence. No public stack traces or unreviewed error-field expansion.
4. Flush at existing significant phase and failure boundaries rather than adding a new timer/service or persisting every output line. A terminal/cancelled path must preserve already-received output without masking the original failure or resuming cancelled work. Describe the last recorded phase as last-known progress, not proof of current execution.
5. For D30, skip database discovery when none are saved, then continue through the saved application/resource graph. Delete only immutable saved IDs after current organization, name, and membership checks. Use logical application deletion where appropriate; do not leave empty application records. Preserve active-operation and dependency guards.
6. Use only fields actually returned by current APIs. Recover restore targets through scoped, exact identity checks, not an assumed `resultJson`. If the historical helper is absent, use existing supported APIs; do not build a replacement validation framework.

## Acceptance and verification

- A synthetic transfer/runtime failure yields a correlated, bounded operator record with its stage/type/code; the operation remains correctly failed or cancelled.
- Sentinel passwords, bearer tokens, SQL credentials and provider-secret suffixes are absent from public responses and emitted operator summaries.
- A quiet phase with fewer than 50 lines is persisted at its existing boundary; failure/cancellation does not discard those received lines. Concurrent append/flush loses or duplicates no records.
- Cleanup with zero managed databases and two owned applications advances to their asynchronous deletion and eventually completes. An empty fixture graph is a successful no-op.
- A foreign resource, unexpected environment, active operation, ambiguous request, or changed saved identity prevents unsafe deletion. Re-running cleanup does not allocate resources or duplicate accepted deletion requests.
- Add focused regressions beside existing worker/logging tests; use synthetic transport/state for the private helper. Run only affected suites before integration. Live cloud work belongs to R6, not this repair.

## Completion / stop condition

Stop when the missing failure evidence and empty-database cleanup case are covered. Do not turn this into a logging redesign or modify operation lifecycle outcomes here; those belong to R4. Merge D10 before another lane edits the processor's failure block.
