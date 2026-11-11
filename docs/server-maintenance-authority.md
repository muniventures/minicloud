# Server maintenance authority

Minicloud keeps one maintenance authority per installation in the application
PostgreSQL database. The packaged [maintenance operations](server-maintenance.md)
(backup, upgrade, restore and host transfer) use it
to stop new management work, wait for accepted work to drain, and revoke an old
host after a transfer. It runs in the existing API/worker processes and needs
no hosted service.

## Durable state

Migration `MaintenanceAuthority` adds three tables:

| Table | Purpose |
|---|---|
| `maintenance_authority` | Singleton row (`id = 1`): `installation_id`, `epoch`, `state` (`open`/`fenced`), `holder_operation_id`, `host_id`, `transfer_operation_id`, timestamps. |
| `maintenance_activities` | Leases for admitted API writes and scheduler passes. Rows renew every 30 s and expire after 2 min. |
| `maintenance_participants` | API and worker processes with the epoch they registered and when they were last seen. |

Each mutation runs in one transaction. It takes `SELECT … FOR UPDATE` on the
authority row with `lock_timeout = 15s`. When the lock wait times out, the
mutation is refused with `maintenance_authority_busy`.

The first mutation binds the row to `installationId`. The bootstrap record
must hold exactly that ID, otherwise the request is refused with
`installation_mismatch`.

## Host identity

Set `Maintenance__HostId` or `Maintenance__HostIdFile` to give the API and
worker processes on one host a stable identifier. Allowed characters are
letters, digits, `.`, `_`, `:` and `-`, up to 128 characters.

Once `transfer` binds `host_id`, any process with a different or missing host
ID is refused.

## Host command

The command reuses the API image and the protected-input rules from the
[bootstrap host interface](self-hosting.md#protected-host-bootstrap-interface):

```sh
dotnet Minicloud.Api.dll maintenance <status|fence|drain-status|release|transfer> \
  (--input-file <protected path> | --stdin)
```

The input is a JSON object of at most 16 KiB with these fields:

```json
{"installationId": "...", "operationId": "...", "hostId": "..."}
```

Unknown or duplicate members are rejected.

| Field | Required for | Notes |
|---|---|---|
| `installationId` | every operation | Must match the bootstrap record. |
| `operationId` | `fence`, `release`, `transfer` | Optional for `drain-status`; when present it must match the holder. |
| `hostId` | `transfer` only | Optional. Defaults to the local `Maintenance:HostId` and must equal it when that is configured. |

The command uses no secrets and writes no output file.

Stdout carries one JSON document:

```json
{"result":"ok|drained|not_drained|refused","operation":"fence","authority":{...}}
{"result":"drained|not_drained","operation":"drain-status","status":{"authority":{...},"phase":"open|draining|drained","drained":true,"blockingCount":0,"work":[{"category":"deployments","state":"running","count":1,"ids":["..."],"blocksDrain":true}],"participants":[...],"observedAt":"..."}}
{"result":"refused","operation":"release","code":"maintenance_holder_mismatch","message":"..."}
```

Input and usage errors go to stderr as `{"code","message"}`.

| Exit | Meaning |
|---|---|
| 0 | Success. For `drain-status`, the installation is fenced, the local host is authorized, the holder matches (when given) and drain is complete. |
| 1 | Input, usage or database error. |
| 2 | `drain-status` is not drained yet. |
| 3 | Refused. Codes: `maintenance_fence_held`, `maintenance_holder_mismatch`, `maintenance_operation_completed`, `maintenance_host_not_authorized`, `maintenance_host_mismatch`, `maintenance_operation_conflict`, `maintenance_authority_busy`, `installation_mismatch`. |

### Operations

- **`fence`** acquires the fence for `operationId`. Repeating the call with the same operation ID succeeds without change. A different holder is refused, and so is reuse of a released operation ID.
- **`release`** reopens the installation. Only the holding operation ID can release it, and the call is idempotent.
- **`transfer`** increments `epoch`, binds `host_id` and leaves the installation fenced under `operationId`. Every process registered at an older epoch, or with a different host ID, is revoked. Call `release` with the same operation ID after the new host has started.
- **`status`** and **`drain-status`** are read-only.

## Enforcement

### API writes

Governed management writes (non-GET controller actions) are checked inside a
transaction that holds the authority row `FOR SHARE` and inserts an activity
lease. A fence therefore either waits for already-admitted writes, which then
show up in `drain-status` until they finish, or refuses them. A request is
never admitted silently.

Refused writes return 503 with an `ErrorResponse`:

- `maintenance_in_progress`, `Retry-After: 30`.
- `maintenance_authority_revoked`, with no retry header.
- `maintenance_authority_unavailable` or `maintenance_admission_unavailable`, `Retry-After: 5`.

These requests keep working during a fence:

- reads and health checks;
- account, session and CLI login flows;
- runtime agent ingest and heartbeats;
- worker-facing deployment callbacks;
- cancellation and image-upload completion for work that was already accepted.

### Workers

Every claim loop (deployments, database operations, app deletions) checks the
authority before it claims anything. The check runs inside the claim
transaction where possible.

While fenced, workers still claim already-accepted queued work so that drain
can finish. They claim nothing once their epoch is superseded or their host
does not match.

New origination stops while fenced: backup scheduling, managed and custom DNS
reconciliation, and log retention. Provider credential cleanup continues as
lease-tracked work.

The deployment heartbeat cancels a running pipeline when its authority is
revoked. Database-operation commits re-check the epoch.

### Drain

These kinds of work block drain:

- **Deployments**: queued, waiting for build, running, or uncertain (finished but still owned).
- **Database operations**: queued, retrying, leased, cancelling, or uncertain. A lease that has expired counts as uncertain, and so does a `*_reconciliation_required` failure with no later completed operation on the same target.
- **Other work**: queued or preparing app deletions, uploading image publications, and active admitted activities.

Expired activity leases, DNS and domain work, credential cleanups and telemetry
are reported for information only. Each work category lists at most 50 IDs.

## Limitations

- Fencing is enforced through the database. A host that can reach the current
  database is revoked once it next checks the epoch. A host running against its
  own restored or copied database never sees the new epoch. An unreachable old
  host that still holds live provider, DNS or SSH credentials is not stopped
  either. Shut the old host down, or rotate those credentials, as part of the
  transfer.
- Before `transfer` binds a host ID, a restarted process registers at the
  current epoch. Configure `Maintenance:HostId` so that a stale host is refused.
- Individual pipeline-step commits are not epoch-checked. A revoked deployment
  stops at the next heartbeat. The deletion claim check takes no row lock,
  because its transaction spans remote deletes.
- Controllers authorize inside actions. During a fence, an authenticated but
  unauthorized governed write therefore receives 503 instead of 403.
  Authentication still runs first.
- `drain-status` reports durable state only. Provider-side actions that were
  never recorded are outside its view.
