# Shared operator bootstrap

The API executable provides one host-only maintenance entry point:

```text
Minicloud.Api bootstrap <operation> --input-file <protected-path>
Minicloud.Api bootstrap <operation> --stdin
```

Run migrations first and supply the normal API database configuration,
`Kratos:AdminBaseUrl` and `Bootstrap:InstallationId` to the eventual browser
transport. This is not a customer CLI command or a public recovery endpoint.
It does not publish or qualify the unfinished self-hosting stack.
The identity administrator uses the packaged Kratos schema ID `user`.

Inputs are bounded JSON objects (64 KiB maximum). Never supply passwords or
setup tokens as command-line arguments. Input files must be regular,
non-symlink Linux files owned by the command's effective user and readable
only by that owner (`0600` or `0400`);
use redirected stdin on platforms without these permission guarantees.
Keep protected inputs and token outputs outside source control.

Every input contains `installationId`; all mutating operations also contain
the original stable `operationId`. Preserve these IDs across restart and
upgrade. A different installation/operation cannot adopt an existing record.

| Operation | Additional input |
| --- | --- |
| `status` | None; returns `{state,expiresAt}` without identity inventory. |
| `prepare` | None; persist an unclaimed installation and issue a setup token. |
| `rotate-token` | None; invalidate the previous token for the same unclaimed installation. |
| `complete` | `setupToken`, `email`, `password`, `displayName`, `organizationName`, `organizationSlug`. |
| `bind-operator` | Explicit existing `userId`, `organizationId`, and a new `password`. The named user must own the named organization. |
| `recover-operator` | The ready installation's bound `userId`, `kratosIdentityId`, and a new `password`. |

`prepare` and `rotate-token` require `--output-file <new-protected-path>`
when stdout is redirected. The command creates that file exclusively with
mode `0600`; it never overwrites an existing file. Otherwise an interactive
terminal may receive the token once. Ordinary result JSON and logs never
contain tokens or passwords. The token expires after 30 minutes; only an
installation/operation-bound SHA-256 verifier is stored. Repeating `prepare`
does not reveal or replace the original token. If protected output fails,
use `rotate-token` with a new output path.

Successful mutations return the existing `BootstrapResult` DTO:
`{installationId,operationId,state,userId?,organizationId?,completedAt?}`.
Host errors return a sanitized code/message on stderr and a nonzero exit.

## Replay and interruption

Host `complete` replay of a verified ready installation returns its original
result **before** checking any secret or contacting Kratos. It does not reset
a password. Browser completion is deliberately different: reuse returns
`setup_already_completed`.

The service persists `in_progress` before identity creation. Kratos private
`metadata_admin.minicloud_bootstrap` contains the exact `installationId`
and `operationId`. An interrupted attempt reconciles only that exact pair;
an email match is never sufficient. If the external outcome is uncertain
and no exact match is visible, the attempt remains `setup_in_progress` and
cannot create another identity. Token rotation can renew admission to this
same pending operation, but cannot reset it or permit another creation.
Do not delete the record to work around uncertainty.

Explicit binding/recovery preserve user/organization IDs, memberships and
independent API keys. They revoke the affected Minicloud browser sessions,
Grafana proxy sessions/unused launch codes, and Kratos identity sessions.
Recovery requires host access, not SMTP.

SQLite serializes host/API processes with a persistent
`<database-path>.bootstrap.lock` sidecar, in addition to an in-process gate.
All processes must use the same canonical database path on a local filesystem;
do not remove or replace the lock file while processes are running.
PostgreSQL uses a session advisory lock held across the pre-mutation commit
and final local transaction. No schema or second seed path is introduced.

## Service integration

Register `AddMinicloudBootstrap()` after the normal API registrations and
inject `BootstrapService`. T42's transport calls:

- `StatusAsync(installationId, cancellationToken)`
- `CompleteBrowserAsync(installationId, CompleteSetupRequest, cancellationToken)`

The browser method loads the operation ID server-side and performs the same
admission/seed/reconciliation logic as `CompleteHostAsync`. Map
`BootstrapException.Code`/`StatusCode` into the existing API error envelope.
The existing capabilities controller still has its earlier state reader;
the account/setup integration owner must switch that reader to this shared
status method before qualifying public mismatched-installation behavior.
The frozen errors are `setup_token_invalid` (401), `setup_token_expired`
(410), `setup_in_progress`, `setup_already_completed`, `setup_state_conflict`
(409), and `auth_unavailable` (503). Dependency failures are errors, never
evidence that an installation needs setup.

The host additionally calls `PrepareAsync`, `RotateTokenAsync`,
`BindOperatorAsync` and `RecoverOperatorAsync`; token issuance requires an
`IBootstrapTokenOutput` implementation that protects the secret. Never expose
binding/recovery or token issuance as anonymous browser operations. T42 still
owns exact-Origin checks, bounded/rate-limited HTTP setup transport, no-store
responses and setup screens; this service does not issue browser sessions.
