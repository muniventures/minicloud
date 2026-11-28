# Sleeping environments

Status: specification only; not implemented or qualified by this document.
Date: 2026-11-28 UTC.

## Outcome

An environment that is not being used stops costing compute. When an environment's server stays idle for a configured time, Selfrun stops its services, captures a provider snapshot image of the server, and deletes the server. The environment is **asleep**. Deploying to it, running `selfrun environments wake`, or selecting **Wake** in the dashboard re-creates the server from that snapshot, restores its routing and storage, and starts the same services. The environment is **awake** again.

The main use is staging and preview environments that are only used during working hours or review. Sleep is opt-in per environment. The sleep timeout is set in the dashboard, through the API, and with the CLI.

## Terms

| Term | Meaning |
| --- | --- |
| Sleep policy | Per-environment setting: disabled, or an idle timeout (`sleepAfter`). |
| Activity | An event that resets the idle timer (see [Idle detection](#idle-detection)). |
| Sleep | Stop services → power off → snapshot → remove managed DNS → delete server. |
| Wake | Create server from snapshot → verify host key → attach storage → start services → restore DNS. |
| Snapshot | Provider image of the server's root disk, owned by Selfrun and labeled with the environment identity. |
| Power state | `awake`, `sleeping`, `asleep`, `waking`, `sleep_failed`, `wake_failed`. |

"Sleep" is the user-facing word. Do not call it "stop" (implies the server still exists and is billed) or "delete" (implies data loss).

## Verified starting point

These paths describe existing source, not the proposed feature.

| Area | Current behavior and source | Consequence |
| --- | --- | --- |
| Provider contract | `modules/common/Providers/IVpsProviderClient.cs` has find/create/status/delete only. `CreateServerRequest.Image` already accepts an image string. `ProviderCapabilities.SupportsSnapshots` exists but is `false` for Hetzner, DigitalOcean, and Vultr (`Providers/Extensibility/ProviderRegistry.cs`). | Need power-off, snapshot create/get/delete, and create-from-snapshot per provider. Flip the capability only after qualification. |
| Provider clients | Hetzner/DigitalOcean pass `request.Image` straight through. Vultr requires a numeric OS ID (`Providers/Vultr/VultrClient.cs`) and creates from snapshots with a different field (`snapshot_id`). | Vultr needs a separate create-from-snapshot path. |
| Provisioning | `modules/worker/Pipeline/Steps/ResolveProvisionServerStep.cs`: `EnsureNewAppAsync` refuses to create a server for any environment that ever had one (succeeded deployment, `ServerInstance`, or `RuntimeServer`). A registered server that is not running fails with "refusing replacement provisioning". | Wake must be an explicit, separately authorized restore path. It must not loosen the replacement guard for ordinary deployments. |
| First boot | `BuildCloudInit` installs packages, Docker, Caddy, firewall, swap, and runs host-key enrollment. `ServerHostKeyEnrollment` proves the new host key with a one-time secret bound to a `ServerInstance` ID. `PersistHostKeyAsync` refuses to replace a verified pin on the same record. | A restored server gets a new provider instance, so cloud-init regenerates host keys. Wake uses a minimal cloud-init (user, keys, enrollment) and a new `ServerInstance` record with its own enrollment. |
| Server identity | `ServerInstance` (`ProviderServerId`, `IpAddress`, `Sku`, `Region`, `Image`, host-key pin) and `RuntimeServer` (`ProviderServerId`, agent token) in `modules/common/Models`. `FindExistingServerAsync` rejects registrations with blank provider IDs or a runtime without a server. | Sleep must retire/replace these records under explicit power state. Other consumers must not treat an asleep environment as a broken one. |
| Routing | `DnsSetupStep` and `DomainDnsReconciler` upsert Cloudflare A records for Selfrun subdomains to `ServerInstance.IpAddress`. Caddy and its TLS state live on the server. | A new IP requires managed DNS updates. Dangling A records to a released IP must be removed while asleep. |
| Custom domains | `CustomDomainVerificationService` verifies that a user-controlled A/AAAA record equals the server IP. | Wake to a new IP breaks unmanaged custom domains. See [Addresses and domains](#addresses-and-domains). |
| Durable storage | `AppStorageVolume` stores a `ProviderVolumeId` retained independently of the server. `AppStorageService.EnsureAsync` attaches a detached volume during `SshDeployStep.Storage`. Hetzner only (`SupportsDurableVolumes`). | Volumes survive server deletion and are not in the snapshot. Wake must re-attach and mount them before services start. |
| Databases on the server | SQLite files and the in-environment `postgres:16-alpine` data directory are on the server's disk (`scripts/github-actions/selfrun-deploy/remote-deploy.sh`). `DatabaseBackupProcessor` SSHes to the environment's `ServerInstance`. Shared/dedicated database servers and backup repositories are internal environments with their own servers. | The snapshot carries app-hosted database data. Database operations must defer while asleep. Internal hosts are never eligible. |
| Containers | Services use `restart: "unless-stopped"`. | Stopping services before power-off keeps them stopped on restored boot until Selfrun mounts storage and starts them. |
| Job model | Deployments: durable rows claimed with `FOR UPDATE … SKIP LOCKED`, ownership-token fencing, one active deployment per environment (`Queue/PostgresDeploymentQueueConsumer.cs`). Deletion and database operations use their own durable operation rows and workers (`Deletion/AppDeletionWorker.cs`, `Databases/DatabaseOperationWorker.cs`). `DatabaseBackupPolicy.NextDueAt` drives periodic scheduling. | Sleep/wake follow the durable-operation + dedicated-worker pattern and share per-environment serialization with deployments. |
| Idle precedent | Coding workspaces had idle shutdown with a configurable timeout, activity-driven countdown cancellation, and claim-time rechecks (#162; code removed in 7d4191e, see `git show 7d4191e^:modules/common/Services/DevelopmentWorkspaceService.cs`). | Reuse those semantics. Do not restore the workspace code. |

## Scope

Included:

- Per-environment sleep policy, power state, and durable sleep/wake operations.
- Automatic sleep after the idle timeout, manual sleep, and wake on deploy, CLI, or dashboard.
- Snapshot lifecycle: create, verify, retain, replace, delete on environment/application deletion.
- Hetzner, DigitalOcean, and Vultr support, each enabled only after live qualification.
- API, CLI, dashboard, and documentation.

Excluded:

- Wake-on-request (holding HTTP requests or serving a "waking" page while asleep). This is a possible follow-up; see [Future work](#future-work).
- Scheduled sleep/wake windows (cron-style "office hours").
- Organization-wide default policy.
- Sleeping internal environments: shared/dedicated database servers, backup repositories, or the control plane itself.
- Moving an environment to a different plan, region, provider, or account on wake.
- Using the snapshot as a backup product. It is a restore image for wake, not a substitute for database backups.

## Eligibility

An environment may enable a sleep policy, and a sleep may start, only when all of these hold:

1. `EnvironmentKind` is `persistent` or `preview` (never `internal`). Production has no structural privilege; any persistent environment may opt in. The confirmation copy explains the downtime.
2. Its provider descriptor has `SupportsSnapshots = true` and passes `ProviderQualificationGate`.
3. Its provider account is enabled.
4. It is not the host of a managed database, network, or backup repository used by another environment.
5. Every public hostname is a managed Selfrun subdomain with `ManagedDomainOptions.ManageDnsRecords = true`, and the environment has no active custom-domain binding. See [Addresses and domains](#addresses-and-domains).
6. No deletion is in progress for the environment or its application.

Enabling a policy on an ineligible environment fails with a specific error code that names the blocking reason. If eligibility is lost later (for example, a custom domain is added), the sweep does not start sleep, and the environment shows the reason.

## Sleep policy

- `sleepAfterMinutes`: `null` (disabled, the default) or an integer from 30 to 43,200 (30 days).
- Stored per environment with a revision. Updates use the existing revision-checked environment PATCH.
- Changing the policy takes effect immediately for scheduling. Lowering it may make an environment due at once; the sweep still applies the claim-time rechecks below. Disabling it cancels a not-yet-committed sleep.
- Policy changes never wake or sleep an environment by themselves.

## Idle detection

`lastActivityAt` is the latest of:

| Activity | Source |
| --- | --- |
| Deployment created, and deployment reached a terminal state | Deployment admission and lifecycle service |
| Wake completed | Power worker |
| Explicit keep-awake (`wake` on an awake environment, dashboard **Keep awake**) | API |
| Policy enabled or changed | API (gives a full timeout after opt-in) |
| HTTP requests to the environment's own hostnames (phase 2) | Runtime agent observation |

An environment is due when `power_state = awake`, the policy is enabled, it is eligible, `now ≥ max(lastActivityAt + sleepAfter, keepAwakeUntil)`, and no deployment, database operation, or power operation is active or queued.

HTTP activity (phase 2): the runtime agent reports, per observation interval, whether Caddy served at least one request whose `Host` matches a hostname configured for this environment, excluding Selfrun's own health probes. Requests to the bare IP and unknown hosts (internet scanners) do not count. Until phase 2 lands, the dashboard and CLI must say that only deployments and explicit keep-awake count as activity, so users are not surprised when a server sleeps during manual testing.

Reading logs, runtime status, or environment details is observation, not activity.

## Power states and transitions

```text
          sleep (idle sweep | manual)              wake (deploy | CLI | dashboard)
 awake ───────────────────────────► sleeping ──► asleep ─────────────────────────► waking ──► awake
   ▲                                   │                                              │
   │      failure before commit:       │                                              │ failure
   └──────── services restarted ◄──────┘                                              ▼
                                       │ failure after commit                    wake_failed ──(retry)──► waking
                                       ▼
                                  sleep_failed ──(retry sleep | retry wake)
```

- One active power operation per environment, enforced by a unique partial index. Power operations and deployments serialize on the environment row lock. The deployment claim query excludes environments with an active power operation, and the power claim excludes environments with an active deployment or database operation.
- Each operation records its trigger (`idle`, `manual_cli`, `manual_dashboard`, `deployment`, `api`), actor, step, attempt count, ownership token, lease, and error code/message, following `DatabaseOperation`.
- The **commit point** of sleep is the provider power-off request. Before it, new activity or a policy change cancels the sleep, and the worker restarts any stopped services. After it, the operation completes or fails forward. It never leaves an awake environment without running services.

## Sleep procedure

1. **Claim and recheck.** Lock the environment row. Recheck eligibility, policy, idleness (for `idle` triggers), and that no other work is active. Set `sleeping`.
2. **Quiesce.** Over SSH, stop each service stack (`docker compose stop`), stop the runtime agent, `sync`. App-hosted PostgreSQL shuts down cleanly with its container. Commit point follows.
3. **Power off** through the provider's graceful shutdown, then a forced power-off after a timeout. Poll until off.
4. **Snapshot.** Create a snapshot labeled `managed-by=selfrun`, organization, environment ID, and operation ID. Its description names the app/environment. Poll until the image is available, then record the provider image ID, size, region, and creation time.
5. **Verify** the snapshot exists, is `available`, and belongs to the account. Without a verified snapshot, never delete the server: power the server back on, start the services, set `awake`, record `snapshot_failed`.
6. **Remove routing.** Delete managed DNS records for the environment's hostnames so no record points at an address that may be reassigned to someone else. Mark domain bindings `asleep`, not `pending`/`mismatch`.
7. **Delete the server** through the registered provider account, reusing the ownership checks from `AppDeletionProcessor`. Poll until it is gone. Confirm durable volumes are detached and retained.
8. **Retire placement.** Close the `ServerInstance` record (status `retired`, kept for audit). Store the placement needed for wake on the snapshot record: provider, account, region, SKU, server name, volume IDs, and personal SSH key references. Clear the `RuntimeServer` provider ID; keep its identity and token.
9. **Finish.** Set `asleep` and `asleepSince`. Delete older Selfrun snapshots for this environment beyond retention. Emit an audit event.

## Wake procedure

1. **Admit.** Wake requests on `asleep`, `sleep_failed` (after deletion), or `wake_failed` create or join the single wake operation. A wake request on `awake` only records keep-awake activity. One on `sleeping` is queued: it waits for the sleep to commit or cancel, then wakes if still needed.
2. **Recheck** that the provider account is enabled and the snapshot still exists.
3. **Create the server** from the snapshot with the recorded server name, SKU, region, labels, and provider SSH key references. Use a minimal wake cloud-init that only ensures the `selfrun` user's authorized key and runs host-key enrollment for a new `ServerInstance` ID. Do not reinstall packages. Before creating, search by name and labels so a retry adopts a server from an interrupted attempt instead of creating a second one.
4. **Wait, then enroll the host key**, reusing `WaitForServerReadyAsync`, `WaitForSshAsync`, and `EnrollHostKeyAsync`. Persist the new `ServerInstance` (active, new IP, new pin) and update `RuntimeServer.ProviderServerId`.
5. **Attach durable volumes** and mount them with the same mount logic used by `SshDeployStep.Storage`.
6. **Start services** of the current release (`docker compose up -d` per stack, then the runtime agent). Run the existing health checks. Selfrun routes in Caddy and TLS state come from the snapshot. Caddy re-validates certificates as usual.
7. **Restore routing.** Upsert managed DNS records to the new IP and wait for propagation, reusing `DnsSetupStep`/`DomainDnsReconciler` code.
8. **Finish.** Set `awake` and `lastActivityAt = now`. Keep the snapshot (see [Retention](#snapshot-retention)). Emit an audit event.

On failure, delete a server created by this attempt only if it never passed host-key enrollment. Otherwise keep it registered for retry. Set `wake_failed` with the error and keep the snapshot. A retry resumes from the recorded step.

### Deploying to an asleep environment

Deployment admission to an `asleep`/`wake_failed` environment creates the deployment and starts a `deployment`-triggered wake immediately. The wake runs while the CLI builds and uploads. The deployment claim waits for the wake to finish. Its event stream shows "Environment staging is asleep; waking from snapshot taken …". If the wake fails, the deployment fails with `environment_wake_failed` and links the power operation. The ordinary `ResolveProvisionServerStep` replacement guard stays unchanged: after a wake, it finds the new registered server.

The reusable GitHub Actions workflow needs no change, but its documented timeout must allow for a wake (several minutes, depending on snapshot size and provider).

## Addresses and domains

The restored server usually gets a new public IPv4/IPv6 address.

- **Managed Selfrun subdomains**: removed at sleep, re-created at wake. Required for eligibility.
- **Unmanaged generated hostnames** (`ManageDnsRecords = false`) and **custom domains** point at the server IP through records the user controls. They would break or, worse, dangle to a reassigned address. In v1 these environments are ineligible.

Retaining a stable address across sleep is a follow-up. Hetzner Primary IPs can survive server deletion. Vultr can convert an instance IP to a reserved IP. DigitalOcean reserved IPs are additional addresses that users must re-point to once. That work would make custom domains eligible. Unassigned retained addresses are billed by the provider.

## Snapshot retention

- Keep exactly one wake snapshot per environment: the latest verified one. Delete the previous snapshot only after a new one is verified.
- After a successful wake, keep the snapshot until the next successful sleep replaces it. It allows recovery if the restored server is lost before the next sleep, but it costs snapshot storage while the environment is awake.
- Environment and application deletion delete all Selfrun snapshots for the environment, after deleting servers. Asleep environments have no server to delete. Deletion must list snapshots as retained billable resources if snapshot deletion fails, like retained volumes.
- A periodic reconciler lists provider images with Selfrun labels per provider account and reports orphans. It does not delete images that cannot be matched to a deleted environment.

## Data and security

- A snapshot contains everything on the server disk: app-hosted databases, deployed secret files, the runtime agent token, Caddy TLS keys, and container images. It has the same trust level as the server disk and stays private to the provider account. Never make a snapshot public or share it across accounts.
- Snapshot IDs, sizes, and timestamps may appear in API responses. No provider credentials or labels with secret material.
- The runtime agent token in the snapshot stays valid. The agent reconnects after wake. Rotating it on wake is optional hardening, not required for v1.
- Host-key trust is re-established for every restored server through enrollment, never copied from the old pin.
- While asleep: `DatabaseBackupProcessor` skips due backups of app-hosted databases and reschedules them for after the next wake. Nothing changes on disk while asleep. Manual database operations, SSH-dependent API actions, and custom-domain verification return `409 environment_asleep` with a wake hint. `DomainDnsReconciler` and `CustomDomainVerificationService` skip asleep environments.
- Authorization: changing policy requires `apps:write`. Sleep and wake require `deployments:create`. Members without these can see the state.

## API

Extend `GET/PATCH /api/apps/{applicationId}/environments/{environmentId}`:

```json
{
  "sleep": {
    "policy": { "sleepAfterMinutes": 120, "revision": 4 },
    "state": "asleep",
    "stateChangedAt": "2026-11-28T18:00:00Z",
    "lastActivityAt": "2026-11-28T16:00:00Z",
    "keepAwakeUntil": null,
    "sleepsAt": null,
    "eligible": true,
    "ineligibleReason": null,
    "snapshot": { "createdAt": "2026-11-28T18:04:12Z", "sizeGb": 9.4 },
    "operation": { "id": "…", "kind": "wake", "status": "running", "step": "attach_storage", "trigger": "manual_cli" },
    "lastError": null
  }
}
```

PATCH accepts `sleep.policy.sleepAfterMinutes` (`null` disables) with `sleep.policy.revision`.

New endpoints:

| Endpoint | Behavior |
| --- | --- |
| `POST /api/apps/{applicationId}/environments/{environmentId}/wake` | Body `{ "keepAwakeMinutes": 240 }` (optional, 30–1,440). `202` with the operation when waking; `200` when already awake (keep-awake recorded). |
| `POST /api/apps/{applicationId}/environments/{environmentId}/sleep` | Manual sleep now. `202` with the operation; `409` with a reason when ineligible or busy. |

Error codes include `environment_asleep`, `environment_waking`, `environment_sleep_ineligible`, `environment_power_busy`, `environment_wake_failed`, and `snapshot_failed`. Update `docs/api.md`.

## CLI

```sh
selfrun environments update staging --sleep-after 2h     # 30m … 30d
selfrun environments update staging --sleep-after off
selfrun environments sleep staging [--no-wait]
selfrun environments wake staging [--keep-awake 4h] [--no-wait]
selfrun environments list                                  # adds STATE and SLEEPS AT columns
selfrun deploy --environment staging                       # wakes automatically, prints wake progress
```

- Durations accept `m`, `h`, and `d` units. The CLI validates bounds before the request.
- `wake` and `sleep` wait by default, print step progress, and exit non-zero on failure with the error code. `--no-wait` prints the operation ID.
- `environments update` with `--sleep-after` alone is valid and does not require SSH options. Today `update` without SSH options is rejected. Relax that rule only when another update option is present.
- `apps inspect` shows the policy, state, and snapshot time.
- Update `docs/cli.md` (environments section and deploy notes) and the command help in `CliApplication.cs`.

## Dashboard

Follow [Apps and operations UX](apps-operations-ux.md) and the [environment model](application-environment-model.md). Sleep state belongs to the selected environment.

- **Environment settings**: **Sleep when idle** switch plus a duration select (30 minutes, 1, 2, 4, 8 hours, 1 day, custom). The help text says the server is deleted and restored from a snapshot, waking takes several minutes, snapshot storage is billed, and which activity counts. Ineligible environments show the switch disabled with the reason.
- **Overview**: a status line, using text and the existing status tokens:
  - Awake with a policy: "Sleeps in about 1 h 40 min · Keep awake".
  - Asleep: "Asleep since 18:04 · snapshot 9.4 GB · Wake".
  - Waking or sleeping: step progress. Retain the current runtime observations as unavailable, not unhealthy.
  - Failed: error and **Retry**.
- **Environment selector**: append "Asleep" or "Waking" text to the environment entry.
- Do not add sleep state to the Apps list columns without amending the approved Apps UX spec.
- Wake and sleep dialogs name the app and environment, and keep focus and announcement behavior per `DESIGN.md`.

## Provider work

Add an `IVpsProviderSnapshotClient` capability interface, separate from `IVpsProviderClient` like `IStorageVolumeProvider`:

```csharp
Task PowerOffAsync(string serverId, CancellationToken ct);           // graceful, then forced
Task PowerOnAsync(string serverId, CancellationToken ct);            // rollback before commit
Task<ProviderSnapshot> CreateSnapshotAsync(string serverId, string description, IReadOnlyDictionary<string, string> labels, CancellationToken ct);
Task<ProviderSnapshot?> GetSnapshotAsync(string snapshotId, CancellationToken ct);
Task<IReadOnlyList<ProviderSnapshot>> ListSnapshotsAsync(IReadOnlyDictionary<string, string> labels, CancellationToken ct);
Task DeleteSnapshotAsync(string snapshotId, CancellationToken ct);   // 404 = success
Task<ProviderServerInfo> CreateServerFromSnapshotAsync(CreateServerRequest request, string snapshotId, CancellationToken ct);
```

| Provider | Notes to qualify against current provider documentation |
| --- | --- |
| Hetzner | `POST /servers/{id}/actions/shutdown` / `poweroff`, `POST /servers/{id}/actions/create_image` (`type: snapshot`, labels), poll the action, `GET/DELETE /images/{id}`, create with `image: <id>`. Snapshots are usable across locations in the project. Volumes auto-detach on server delete. |
| DigitalOcean | Droplet `shutdown`/`power_off` and `snapshot` actions. Snapshot images are regional; restore in the recorded region. Tags instead of labels. Create with `image: <id>`. |
| Vultr | `POST /instances/{id}/halt`, `POST /snapshots` with `instance_id`, poll status, create instance with `snapshot_id` (not `os_id`). Tag/description for ownership. |

Each provider needs fake-HTTP tests in `modules/tests/Selfrun.Worker.Tests/Providers` using `TestHttpMessageHandler`. Live qualification must confirm, per provider:

1. Cloud-init runs per-instance modules on a server created from the snapshot, regenerates SSH host keys, and runs the enrollment script.
2. Services stay stopped on boot until started.
3. Snapshot and restore times and sizes for a representative environment.
4. The billing behavior noted in user docs.

Set `SupportsSnapshots = true` only for providers that pass.

## Data model

Names may follow repository conventions. Responsibilities must stay separate.

- `environment_sleep_settings` (1:1 with environment): `sleep_after_minutes`, `revision`, `power_state`, `state_changed_at`, `last_activity_at`, `last_activity_kind`, `keep_awake_until`, `last_error_code`, `last_error_message`.
- `environment_power_operations`: `id`, `organization_id`, `environment_id`, `kind` (`sleep`/`wake`), `trigger`, `actor_json`, `status`, `step`, `ownership_token`, `lease_expires_at`, `attempt_count`, `error_code`, `error_message`, `created_at`, `updated_at`, `completed_at`, `deployment_id` (for deployment-triggered wake). Unique partial index: one non-terminal operation per environment.
- `environment_snapshots`: `id`, `environment_id`, `organization_id`, `provider`, `provider_account_id`, `provider_image_id`, `region`, `sku`, `server_name`, `size_gb`, `status` (`creating`, `available`, `superseded`, `deleting`, `deleted`, `failed`), `placement_json` (volume IDs, SSH key references), `created_at`, `deleted_at`.
- `ServerInstance.Status` gains `retired`. All consumers that read "the environment's server" select the single non-retired record. Audit every `db.ServerInstances` consumer: `FindExistingServerAsync`, `SaveServerInstanceAsync`, `EnsureNewAppAsync`, `DomainBindingService`, `CustomDomainVerificationService`, `DomainDnsReconciler`, `DatabaseBackupProcessor`, `DatabaseOperationProcessor`, `DatabaseNetworkProcessor`, `DatabaseMigrationProcessor`, `DatabasesController`, `ApplicationsController`, `DeploymentsController`, `RuntimeController`, `AppDeletionService/Processor`, `AppSummaryReader`, `SshHostKeyTrust`, provider account/catalog services.

The migration is additive. Existing environments get `power_state = awake` and a disabled policy.

## Workers

- **Power worker**: claims power operations (`FOR UPDATE … SKIP LOCKED`, maintenance-authority aware like the deployment queue), heartbeats its lease, fences with its ownership token, and runs the sleep or wake procedure as resumable steps.
- **Idle sweep**: runs every minute. It finds due environments and inserts `idle` sleep operations idempotently. The claim repeats every check under the row lock.
- **Snapshot reconciler**: runs hourly. It retries deleting superseded snapshots and reports orphans.

All three register as maintenance participants so maintenance can fence and drain them.

## Implementation breakdown

Proposed native sub-issues under one coordinator issue. Dependencies are explicit; items without dependencies can run in parallel.

| # | Deliverable | Ownership | Depends on | Effort |
| --- | --- | --- | --- | --- |
| 1 | Provider snapshot/power capability + Hetzner, DigitalOcean, Vultr clients and fake-HTTP tests | `modules/common/Providers/**`, provider tests | — | Medium |
| 2 | Data model + migration (settings, operations, snapshots, `retired` servers) and `ServerInstance` consumer audit | `modules/common/Models`, `Migrations`, consumer services | — | Medium / migration |
| 3 | API: policy PATCH, wake/sleep endpoints, environment DTO, eligibility, `environment_asleep` guards, deletion of snapshots | `modules/api`, `modules/contracts`, `modules/common/Services` | 2 | Large |
| 4 | Power worker: sleep procedure with rollback before commit | `modules/worker/Power/**` | 1, 2 | Large |
| 5 | Power worker: wake procedure, deployment-triggered wake, deployment claim exclusion | `modules/worker/Power/**`, `Queue`, deployment admission | 1, 2, 4 | Large |
| 6 | Idle sweep, activity recording, database/DNS/domain workers skipping asleep environments | `modules/worker`, `modules/common/Services` | 2, 3 | Medium |
| 7 | CLI commands, help, and `docs/cli.md` | `modules/cli`, docs | 3 | Small–Medium |
| 8 | Dashboard settings, Overview status/actions, selector text | `modules/dashboard` | 3 | Medium / visual verification |
| 9 | Runtime agent HTTP activity signal (phase 2) | `modules/runtime-agent`, runtime ingestion | 2, 6 | Medium |
| 10 | Live provider qualification and capability enablement | provider descriptors, docs | 1, 4, 5 | Live operations |

## Acceptance

| ID | Required result |
| --- | --- |
| S1 | Policy can be enabled, changed, and disabled from the dashboard, API, and CLI with bounds validation, revision checks, authorization, and persistence across restarts. Ineligible environments are rejected with the specific reason. |
| S2 | With a fake clock, an eligible environment sleeps once `sleepAfter` elapses after its last activity, and not before. Activity, policy disable, or a queued deployment before the commit point cancels the sleep and leaves services running. |
| S3 | Sleep never deletes a server without a verified snapshot. Snapshot failure restores a running, awake environment. |
| S4 | After sleep, the provider server is deleted, managed DNS records are removed, durable volumes are retained and detached, and the `ServerInstance` is retired. Database backups, domain verification, and DNS reconciliation do not report errors for the asleep environment. |
| S5 | `selfrun environments wake`, the dashboard Wake button, and `selfrun deploy` each restore the environment. The new server has a newly enrolled host key, the same disk data (SQLite and app-hosted PostgreSQL rows written before sleep), re-attached volume data, running services passing health checks, and managed hostnames resolving to the new IP. |
| S6 | A deployment to an asleep environment wakes it, then deploys the new release, in one CLI invocation and in the GitHub Actions workflow. |
| S7 | Interrupted sleep or wake (worker killed at each step) resumes or fails safely: no duplicate servers, no deleted snapshot without a replacement, no environment left without both a server and a snapshot. |
| S8 | Only one power operation or deployment runs per environment; concurrent wake requests join one operation. |
| S9 | Deleting an asleep environment or its application deletes its snapshots, or lists them as retained billable resources with a retry. |
| S10 | Internal environments, environments with custom domains, and unmanaged-DNS environments can never sleep. |
| S11 | Dashboard shows awake/sleeping/asleep/waking/failed states accessibly at 320–390 px and desktop widths. Runtime observations of an asleep environment read as unavailable, not unhealthy. |
| S12 | Each enabled provider passed live qualification of the provider checks listed above, with evidence on the qualification issue. |

Verification: focused .NET provider, worker, API, and CLI tests; PostgreSQL migration and concurrency tests; dashboard tests and build; browser checks of the settings and Overview flows; and a live sleep/wake cycle on a staging environment for each qualified provider. Follow `README.md` for local checks.

## Open questions

1. **HTTP activity in v1 or phase 2?** Recommended: phase 2 (issue 9). v1 counts deployments and explicit keep-awake only and says so in the UI.
2. **Stable addresses.** Recommended: out of v1. Custom-domain environments stay ineligible until a retained-IP follow-up.
3. **Snapshot kept after wake.** Recommended: keep (safer, small storage cost). Alternative: delete after a successful wake to minimize cost.
4. **Default timeout suggestion** in the dashboard when enabling: recommended 2 hours.

## Future work

- Wake-on-request: point managed hostnames at the control plane while asleep, serve a "waking" page, and trigger wake on the first request.
- Scheduled sleep and wake windows.
- Organization default policy for new preview environments.
- Retained IPs to support custom domains.
