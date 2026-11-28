# Sleeping environments

Status: specification only; not implemented or qualified by this document.
Date: 2026-11-28 UTC.

## Outcome

An environment that is not being used stops costing compute. When an environment's server stays idle for a configured time, Selfrun stops its services, captures a provider snapshot image of the server, and deletes the server. The environment is **asleep**. Deploying to it, running `selfrun environments wake`, or selecting **Wake** in the dashboard re-creates the server from that snapshot, restores its routing and storage, and starts the same services. The environment is **awake** again.

The main use is staging and preview environments that are only used during working hours or review. Sleep is opt-in per environment. The sleep timeout (default 2 hours) and whether HTTP traffic counts as activity are set in the dashboard, through the API, and with the CLI.

Every hostname keeps working after wake. When all of an environment's hostnames are in DNS that Selfrun manages through Cloudflare, Selfrun points them at the restored server's new address, so no reserved address is needed. When any hostname is managed outside Selfrun, such as a custom domain or a manual record, the environment keeps a reserved public IPv4 address across sleep. See [Addresses](#addresses). Only one snapshot exists per environment: each sleep replaces it, and it is kept after wake.

## Terms

| Term | Meaning |
| --- | --- |
| Sleep policy | Per-environment settings: disabled, or an idle timeout (`sleepAfter`, default 2 hours), plus whether HTTP traffic counts as activity. |
| Activity | An event that resets the idle timer (see [Idle detection](#idle-detection)). |
| Address mode | `dynamic`: every hostname is Selfrun-managed Cloudflare DNS, and the restored server may get a new address. `retained`: at least one hostname is managed outside Selfrun, so the environment keeps a reserved address. Derived, not chosen by the user. |
| Retained address | A provider-reserved public IPv4 address owned by the environment, independent of any one server. Used only in `retained` mode. |
| Sleep | Stop services → power off → new snapshot → purge old snapshot → remove managed DNS records (dynamic) → delete server. |
| Wake | Create server from snapshot (on the retained address, if any) → verify host key → attach storage → start services → publish managed DNS records (dynamic). |
| Snapshot | Provider image of the server's root disk, owned by Selfrun and labeled with the environment identity. At most one per environment. |
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
| Routing | One operator-configured Cloudflare zone (`CloudflareDnsClient`, `ZoneId`) holds hostnames under the managed suffix (`ManagedDomainOptions.Contains`). When `ManagedDomainOptions.ManageDnsRecords` is true, `DnsSetupStep` and `DomainDnsReconciler` upsert A records (IPv4 only, TTL 120, not proxied by default) to `ServerInstance.IpAddress`, and `ICloudflareDnsClient` can delete them and wait for propagation. Manual-DNS instructions suggest an optional AAAA record. Caddy and its TLS state live on the server. | Managed hostnames can follow a new address on wake, so they need no reserved address. Manual instructions must stop suggesting AAAA for sleep-enabled environments. |
| Custom domains | Custom domains live in DNS the user controls; Selfrun never writes them. `CustomDomainVerificationService` requires every A/AAAA answer to equal the server's IPv4 address. | Any custom domain, or `ManageDnsRecords = false`, requires a retained address. See [Addresses](#addresses). |
| Server addresses | Servers use the provider's ephemeral public IP, released when the server is deleted. No reserved/primary IP support exists in the provider clients. | Add address retention per provider. |
| Durable storage | `AppStorageVolume` stores a `ProviderVolumeId` retained independently of the server. `AppStorageService.EnsureAsync` attaches a detached volume during `SshDeployStep.Storage`. Hetzner only (`SupportsDurableVolumes`). | Volumes survive server deletion and are not in the snapshot. Wake must re-attach and mount them before services start. |
| Databases on the server | SQLite files and the in-environment `postgres:16-alpine` data directory are on the server's disk (`scripts/github-actions/selfrun-deploy/remote-deploy.sh`). `DatabaseBackupProcessor` SSHes to the environment's `ServerInstance`. Shared/dedicated database servers and backup repositories are internal environments with their own servers. | The snapshot carries app-hosted database data. Database operations must defer while asleep. Internal hosts are never eligible. |
| Containers | Services use `restart: "unless-stopped"`. | Stopping services before power-off keeps them stopped on restored boot until Selfrun mounts storage and starts them. |
| Job model | Deployments: durable rows claimed with `FOR UPDATE … SKIP LOCKED`, ownership-token fencing, one active deployment per environment (`Queue/PostgresDeploymentQueueConsumer.cs`). Deletion and database operations use their own durable operation rows and workers (`Deletion/AppDeletionWorker.cs`, `Databases/DatabaseOperationWorker.cs`). `DatabaseBackupPolicy.NextDueAt` drives periodic scheduling. | Sleep/wake follow the durable-operation + dedicated-worker pattern and share per-environment serialization with deployments. |
| Idle precedent | Coding workspaces had idle shutdown with a configurable timeout, activity-driven countdown cancellation, and claim-time rechecks (#162; code removed in 7d4191e, see `git show 7d4191e^:modules/common/Services/DevelopmentWorkspaceService.cs`). | Reuse those semantics. Do not restore the workspace code. |

## Scope

Included:

- Per-environment sleep policy (timeout and HTTP-traffic activity setting), power state, and durable sleep/wake operations.
- Automatic sleep after the idle timeout, manual sleep, and wake on deploy, CLI, or dashboard.
- HTTP request activity reported by the runtime agent.
- Dynamic addresses with managed DNS updates for Cloudflare-managed environments, and retained public IPv4 addresses for environments with other hostnames.
- Snapshot lifecycle: create, verify, replace (one at a time), delete on environment/application deletion.
- Hetzner, DigitalOcean, and Vultr support, each enabled only after live qualification.
- API, CLI, dashboard, and documentation.

Excluded:

- Wake-on-request (holding HTTP requests or serving a "waking" page while asleep). This is a possible follow-up; see [Future work](#future-work).
- Scheduled sleep/wake windows (cron-style "office hours").
- Organization-wide default policy.
- Sleeping internal environments: shared/dedicated database servers, backup repositories, or the control plane itself.
- Moving an environment to a different plan, region, provider, or account on wake.
- Retaining IPv6 addresses. Sleep-enabled environments are reachable over IPv4 only, as managed records and custom domains already are.
- Writing DNS for custom domains through a user's own Cloudflare account. Custom domains use `retained` mode in v1.
- Letting the user force `retained` mode for a fully managed environment.
- Using the snapshot as a backup product. It is a restore image for wake, not a substitute for database backups.

## Eligibility

An environment may enable a sleep policy, and a sleep may start, only when all of these hold:

1. `EnvironmentKind` is `persistent` or `preview` (never `internal`). Production has no structural privilege; any persistent environment may opt in. The confirmation copy explains the downtime.
2. Its provider descriptor has `SupportsSnapshots = true` and passes `ProviderQualificationGate`. In `retained` mode, it must also have `SupportsRetainedAddresses = true`.
3. Its provider account is enabled.
4. It is not the host of a managed database, network, or backup repository used by another environment.
5. For sleep to start (not to enable the policy): in `retained` mode, a retained address is assigned to the current server, and on DigitalOcean every verified custom domain resolves to it. In `dynamic` mode, managed DNS is reachable (Cloudflare configured and the maintenance fence open).
6. No deletion is in progress for the environment or its application.

Custom domains and manually managed DNS do not block sleep. They switch the environment to `retained` mode.

Enabling a policy on an ineligible environment fails with a specific error code that names the blocking reason. If eligibility is lost later, the sweep does not start sleep, and the environment shows the reason.

## Sleep policy

- `sleepAfterMinutes`: `null` (disabled, the default for every environment) or an integer from 30 to 43,200 (30 days). Enabling without a value uses **120 minutes (2 hours)**, in the API, CLI, and dashboard.
- `httpTrafficIsActivity`: boolean, default `true`. When `true`, HTTP requests to the environment's hostnames reset the idle timer. When `false`, only control-plane activity does, so a server can sleep on schedule even if monitors or crawlers keep hitting it.
- Stored per environment with a revision. Updates use the existing revision-checked environment PATCH.
- In `retained` mode, enabling the policy also reserves the environment's address (see [Addresses](#addresses)). The policy is saved only after the reservation succeeds, or the request fails with the provider error. In `dynamic` mode, enabling reserves nothing.
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
| HTTP requests to the environment's own hostnames, only when `httpTrafficIsActivity = true` | Runtime agent observation |

An environment is due when `power_state = awake`, the policy is enabled, it is eligible, `now ≥ max(lastActivityAt + sleepAfter, keepAwakeUntil)`, and no deployment, database operation, or power operation is active or queued.

HTTP activity: the runtime agent always reports, per observation interval, the time of the latest request Caddy served whose `Host` matches a hostname configured for this environment, excluding Selfrun's own health probes. Requests to the bare IP and unknown hosts (internet scanners) do not count. The control plane stores it as `lastHttpRequestAt` regardless of the setting. It feeds `lastActivityAt` only when `httpTrafficIsActivity = true`, so toggling the setting takes effect immediately. Source of the signal: Caddy per-host request metrics or a structured access log that the agent reads locally. Request contents and client addresses are not sent to the control plane. A missing or stale agent observation never counts as activity and never blocks sleep.

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
4. **Create the new snapshot.** Create a snapshot labeled `managed-by=selfrun`, organization, environment ID, and operation ID. Its description names the app/environment. Poll until the image is available, then record the provider image ID, size, region, and creation time as `pending`.
5. **Verify** the new snapshot exists, is `available`, and belongs to the account. Without a verified snapshot, never delete the server or the old snapshot: power the server back on, start the services, set `awake`, record `snapshot_failed`.
6. **Purge the old snapshot.** Make the new snapshot `current` and the previous one `superseded`, then delete the previous one from the provider (404 counts as deleted). Retry transient failures within the step. If deletion still fails, continue the sleep. Mark the old snapshot `deleting` for the reconciler and show a warning: a second image exists only until that retry succeeds.
7. **Remove or keep addresses.**
   - In `dynamic` mode, delete the environment's managed A records through `ICloudflareDnsClient.DeleteDnsRecordsAsync` before deleting the server. A record must never point at a released address that the provider can give to another customer. If removal keeps failing, leave the server powered off, set `sleep_failed`, and retry. A stopped server is safe; a stale record is not.
   - In `retained` mode, confirm that the retained address is configured to survive server deletion. DNS is untouched.
8. **Delete the server** through the registered provider account, reusing the ownership checks from `AppDeletionProcessor`. Poll until the server is gone. Confirm durable volumes (and the retained address, if any) are detached and still exist.
9. **Retire placement.** Close the `ServerInstance` record (status `retired`, kept for audit). Store the placement needed for wake on the snapshot record: provider, account, region or datacenter, SKU, server name, volume IDs, personal SSH key references, and the address mode. Clear the `RuntimeServer` provider ID; keep its identity and token. Domain bindings keep their state. `DomainDnsReconciler` and `CustomDomainVerificationService` skip asleep environments, so they do not recreate removed records or mark bindings pending or mismatched.
10. **Finish.** Set `asleep` and `asleepSince`. Emit an audit event.

## Wake procedure

1. **Admit.** Wake requests on `asleep`, `sleep_failed` (after deletion), or `wake_failed` create or join the single wake operation. A wake request on `awake` only records keep-awake activity. One on `sleeping` is queued: it waits for the sleep to commit or cancel, then wakes if still needed.
2. **Recheck** that the provider account is enabled, that the current snapshot still exists, and in `retained` mode that the retained address still exists.
3. **Create the server** from the snapshot with the recorded server name, SKU, region or datacenter, labels, and provider SSH key references. In `retained` mode, create it on the retained address (see [Addresses](#addresses)); in `dynamic` mode, accept the provider-assigned address. Use a minimal wake cloud-init that only ensures the `selfrun` user's authorized key and runs host-key enrollment for a new `ServerInstance` ID. Do not reinstall packages. Before creating, search by name and labels so a retry adopts a server from an interrupted attempt instead of creating a second one.
4. **Wait, then enroll the host key**, reusing `WaitForServerReadyAsync`, `WaitForSshAsync`, and `EnrollHostKeyAsync`. In `retained` mode, wait until the retained address is assigned and reachable. Persist the new `ServerInstance` (active, new pin, and the retained IP or the new provider IP) and update `RuntimeServer.ProviderServerId`. `SshHostKeyTrust` already requires that no other active registration shares the IP. The retired record does not count as active.
5. **Attach durable volumes** and mount them with the same mount logic used by `SshDeployStep.Storage`.
6. **Start services** of the current release (`docker compose up -d` per stack, then the runtime agent). Run the existing health checks. Selfrun routes in Caddy and TLS state come from the snapshot; Caddy routes by hostname, so a new address needs no on-server change. Caddy re-validates certificates as usual.
7. **Publish routing.**
   - In `dynamic` mode, upsert every managed A record to the new address through the same path as `DomainDnsReconciler`, then wait for propagation with `WaitForPropagationAsync`. Records use the managed TTL (120 seconds by default), so clients that cached the removal see the server within that time.
   - In `retained` mode, DNS already points at the retained address. Verify managed records still match it, and repair them if they drifted. Re-run custom-domain verification without changing bindings that are already verified.
8. **Finish.** Set `awake` and `lastActivityAt = now`. Keep the snapshot (see [Snapshot retention](#snapshot-retention)). Emit an audit event.

On failure, delete a server created by this attempt only if it never passed host-key enrollment. Otherwise keep it registered for retry. Set `wake_failed` with the error and keep the snapshot. A retry resumes from the recorded step.

### Deploying to an asleep environment

Deployment admission to an `asleep`/`wake_failed` environment creates the deployment and starts a `deployment`-triggered wake immediately. The wake runs while the CLI builds and uploads. The deployment claim waits for the wake to finish. Its event stream shows "Environment staging is asleep; waking from snapshot taken …". If the wake fails, the deployment fails with `environment_wake_failed` and links the power operation. The ordinary `ResolveProvisionServerStep` replacement guard stays unchanged: after a wake, it finds the new registered server.

The reusable GitHub Actions workflow needs no change, but its documented timeout must allow for a wake (several minutes, depending on snapshot size and provider).

## Addresses

### Address mode

Selfrun derives the mode from the environment's hostnames and recomputes it whenever a domain binding changes:

- **`dynamic`**: every enabled hostname is under the managed suffix (`ManagedDomainOptions.Contains`) and `ManageDnsRecords` is true. Selfrun owns all the DNS, so it removes the records at sleep and publishes them for the new address at wake. Nothing is reserved and there is no address cost while asleep.
- **`retained`**: at least one enabled hostname is a custom domain, or the operator disabled managed DNS records. Selfrun cannot rewrite those records, so the address must not change.

Mode changes while the policy is enabled:

- Adding a custom domain to a `dynamic` environment reserves an address first, as when the policy is enabled. The binding is created only after the reservation succeeds. The environment must be awake: domain binding already requires the server.
- Removing the last custom domain keeps the reservation and stays `retained`. This avoids churn and a second address change on DigitalOcean. Releasing then happens only on environment deletion.

### Retained addresses

In `retained` mode the environment owns one provider-reserved public IPv4 address. Every server it runs on uses that address, so A records, manually managed DNS, and custom domains never change across sleep and wake. No DNS record ever points at an address that could be reassigned to another provider customer.

| Provider | Reserve (at policy enable or first custom domain) | Keep through sleep | Use on wake |
| --- | --- | --- | --- |
| Hetzner | The server's existing Primary IPv4 becomes the retained address. Same address, no DNS change. | Set the Primary IP's `auto_delete` to `false` before deleting the server. | Create the server with `public_net.ipv4` set to the Primary IP ID, in the Primary IP's datacenter (Primary IPs are datacenter-bound; record it). |
| Vultr | Convert the instance's current IPv4 to a reserved IP. Same address, no DNS change. | Reserved IPs survive instance deletion. | Create the instance with `reserved_ipv4` set to the reserved IP ID in the same region. |
| DigitalOcean | Create a reserved IPv4 in the droplet's region and assign it. This is a **new address**. | Reserved IPs survive droplet deletion. | Create the droplet, then assign the reserved IP and wait for the assignment action. |

Rules:

- Reserving happens while the environment is awake, so any address change is visible before the first sleep.
- **DigitalOcean address change.** Selfrun moves managed A records to the reserved address immediately. The dashboard and CLI warn that custom domains and manually managed records must be re-pointed. They show both addresses, and the old one keeps working while the droplet exists. Custom-domain bindings show the new required address. Sleep is not allowed until every verified custom domain resolves to the reserved address, so a domain can never be left pointing at a released IP. A custom domain added after reservation is told the reserved address from the start.
- `ServerInstance.IpAddress` is the address used for SSH, DNS, and domain verification. For DigitalOcean, that is the reserved address once it is assigned and reachable.
- Disabling the policy keeps the reservation, so re-enabling never changes the address again. Releasing happens only on environment or application deletion, after the server is deleted. Hetzner: delete the Primary IP if no server holds it. Vultr and DigitalOcean: delete the reserved IP. Deletion lists a retained address as a billable resource if releasing fails, like retained volumes.
- Unassigned reserved addresses are billed by the provider while the environment is asleep. User docs state this without quoting prices.

### Both modes

- IPv6 is not retained or published. Manual-DNS instructions for sleep-enabled environments say to remove AAAA records.
- While asleep in `dynamic` mode, managed hostnames do not resolve. That is expected; wake-on-request is [future work](#future-work).

## Snapshot retention

- **At most one snapshot per environment.** Each sleep creates and verifies a new snapshot, purges the previous one, and only then deletes the server. Two images exist only between the new snapshot's verification and the old one's purge. A failed purge is retried by the reconciler and shown as a warning.
- The snapshot is **kept after wake** until the next sleep replaces it. It allows recovery if the restored server is lost while awake, at the cost of snapshot storage while awake.
- A wake always restores from the current snapshot. Older snapshots are never used, even while one is still waiting for purge.
- Environment and application deletion delete the environment's snapshot (and any `deleting` leftovers) after deleting servers. Asleep environments have no server to delete. Deletion lists snapshots as retained billable resources if snapshot deletion fails, like retained volumes.
- A periodic reconciler retries purges and lists provider images with Selfrun labels per provider account to report orphans. It does not delete images it cannot match to a superseded snapshot or a deleted environment.

## Data and security

- A snapshot contains everything on the server disk: app-hosted databases, deployed secret files, the runtime agent token, Caddy TLS keys, and container images. It has the same trust level as the server disk and stays private to the provider account. Never make a snapshot public or share it across accounts.
- Snapshot IDs, sizes, and timestamps may appear in API responses. No provider credentials or labels with secret material.
- The runtime agent token in the snapshot stays valid. The agent reconnects after wake. Rotating it on wake is optional hardening, not required for v1.
- Host-key trust is re-established for every restored server through enrollment, never copied from the old pin.
- While asleep: `DatabaseBackupProcessor` skips due backups of app-hosted databases and reschedules them for after the next wake. Nothing changes on disk while asleep. Manual database operations, SSH-dependent API actions, and custom-domain verification return `409 environment_asleep` with a wake hint. `DomainDnsReconciler` and `CustomDomainVerificationService` skip asleep environments.
- Authorization: changing policy requires `apps:write`. Sleep and wake require `deployments:create`. Members without these can see the state. In `retained` mode, enabling the policy or adding a custom domain reserves a billable provider address. It uses the environment's provider account under the same `apps:write` authorization as plan selection. It does not require `providers:write`.

## API

Extend `GET/PATCH /api/apps/{applicationId}/environments/{environmentId}`:

```json
{
  "sleep": {
    "policy": { "sleepAfterMinutes": 120, "httpTrafficIsActivity": true, "revision": 4 },
    "state": "asleep",
    "stateChangedAt": "2026-11-28T18:00:00Z",
    "lastActivityAt": "2026-11-28T16:00:00Z",
    "lastHttpRequestAt": "2026-11-28T15:58:41Z",
    "keepAwakeUntil": null,
    "sleepsAt": null,
    "eligible": true,
    "ineligibleReason": null,
    "addressMode": "retained",
    "retainedAddress": { "ipv4": "203.0.113.10", "state": "unassigned", "previousIpv4": null },
    "snapshot": { "createdAt": "2026-11-28T18:04:12Z", "sizeGb": 9.4, "pendingPurge": false },
    "operation": { "id": "…", "kind": "wake", "status": "running", "step": "attach_storage", "trigger": "manual_cli" },
    "lastError": null
  }
}
```

PATCH accepts `sleep.policy.enabled`, `sleep.policy.sleepAfterMinutes`, and `sleep.policy.httpTrafficIsActivity` with `sleep.policy.revision`. `enabled: false` or `sleepAfterMinutes: null` disables sleep. `enabled: true` without minutes stores 120 on first enable and keeps the stored value afterwards. Omitted fields keep their current values. `addressMode` is `dynamic` or `retained`; `retainedAddress` is `null` in `dynamic` mode. In `retained` mode, the first enable reserves the address synchronously. When that changes the address (DigitalOcean), the response includes `retainedAddress.previousIpv4`. Creating a custom-domain binding on a sleep-enabled `dynamic` environment reserves the address the same way and can fail with `address_reservation_failed`. The first enable runs the address reservation synchronously; when it changes the address (DigitalOcean), the response includes `retainedAddress.previousIpv4`.

New endpoints:

| Endpoint | Behavior |
| --- | --- |
| `POST /api/apps/{applicationId}/environments/{environmentId}/wake` | Body `{ "keepAwakeMinutes": 240 }` (optional, 30–1,440). `202` with the operation when waking; `200` when already awake (keep-awake recorded). |
| `POST /api/apps/{applicationId}/environments/{environmentId}/sleep` | Manual sleep now. `202` with the operation; `409` with a reason when ineligible or busy. |

Error codes include `environment_asleep`, `environment_waking`, `environment_sleep_ineligible`, `environment_power_busy`, `environment_wake_failed`, `snapshot_failed`, `address_reservation_failed`, and `custom_domain_not_repointed`. Update `docs/api.md`.

## CLI

```sh
selfrun environments update staging --sleep-after on        # enable with the 2h default
selfrun environments update staging --sleep-after 4h        # 30m … 30d
selfrun environments update staging --sleep-after off
selfrun environments update staging --traffic-activity off  # only deploys/keep-awake count; default on
selfrun environments sleep staging [--no-wait]
selfrun environments wake staging [--keep-awake 4h] [--no-wait]
selfrun environments list                                   # adds STATE and SLEEPS AT columns
selfrun deploy --environment staging                        # wakes automatically, prints wake progress
```

- Durations accept `m`, `h`, and `d` units. The CLI validates bounds before the request.
- `--sleep-after` and `--traffic-activity` can be combined in one update.
- When enabling (or `domains add`) reserves a new address (DigitalOcean), the CLI prints the old and new address and the custom domains that must be re-pointed. For `dynamic` environments, it says that managed hostnames follow the server automatically.
- `wake` and `sleep` wait by default, print step progress, and exit non-zero on failure with the error code. `--no-wait` prints the operation ID.
- `environments update` with only sleep options is valid and does not require SSH options. Today `update` without SSH options is rejected. Relax that rule only when another update option is present.
- `apps inspect` shows the policy, state, address mode, retained address, and snapshot time.
- Update `docs/cli.md` (environments section and deploy notes) and the command help in `CliApplication.cs`.

## Dashboard

Follow [Apps and operations UX](apps-operations-ux.md) and the [environment model](application-environment-model.md). Sleep state belongs to the selected environment.

- **Environment settings**:
  - **Sleep when idle** switch plus a duration select (30 minutes, 1, **2 (default)**, 4, 8 hours, 1 day, custom).
  - **Count website traffic as activity** switch (default on). Its help text: with it off, the environment sleeps after the timeout even if people or monitors are using it.
  - General help text says the server is deleted and restored from a snapshot, waking takes several minutes, and snapshot storage is billed by the provider.
  - Address text depends on the mode. `dynamic`: "Selfrun updates your *.<suffix> DNS records when the environment wakes. The server's IP address may change." `retained`: "Custom domains need a fixed address. Selfrun keeps a reserved IP for this environment; the provider bills it while the environment is asleep."
  - Ineligible environments show the controls disabled with the reason.
  - On DigitalOcean in `retained` mode, the first reservation confirms the address change, lists the custom domains to re-point, and shows their re-point status until they resolve to the new address.
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
Task<ProviderServerInfo> CreateServerFromSnapshotAsync(CreateServerRequest request, string snapshotId, string? retainedAddressId, CancellationToken ct);
```

And an `IVpsProviderAddressClient` capability interface for retained addresses (`SupportsRetainedAddresses`):

```csharp
Task<ProviderRetainedAddress> RetainServerAddressAsync(string serverId, CancellationToken ct); // Hetzner/Vultr: keep current IPv4; DigitalOcean: create + assign
Task<ProviderRetainedAddress?> GetRetainedAddressAsync(string addressId, CancellationToken ct);
Task EnsureSurvivesServerDeletionAsync(string addressId, CancellationToken ct);             // Hetzner auto_delete=false; no-op elsewhere
Task AssignRetainedAddressAsync(string addressId, string serverId, CancellationToken ct);   // DigitalOcean after create; no-op when set at create
Task ReleaseRetainedAddressAsync(string addressId, CancellationToken ct);                  // 404 = success
```

| Provider | Notes to qualify against current provider documentation |
| --- | --- |
| Hetzner | `POST /servers/{id}/actions/shutdown` / `poweroff`, `POST /servers/{id}/actions/create_image` (`type: snapshot`, labels), poll the action, `GET/DELETE /images/{id}`, create with `image: <id>`. Snapshots are usable across locations in the project. Volumes auto-detach on server delete. Primary IPs: `GET/PUT/DELETE /primary_ips/{id}` (`auto_delete`), create with `public_net.ipv4` and the Primary IP's `datacenter`. |
| DigitalOcean | Droplet `shutdown`/`power_off` and `snapshot` actions. Snapshot images are regional; restore in the recorded region. Droplet snapshot creation cannot set tags, so ownership labels are encoded in the snapshot name. Create with numeric `image: <id>`. Reserved IPs: `POST /reserved_ips` (region), `POST /reserved_ips/{ip}/actions` (`assign`/`unassign`), `DELETE /reserved_ips/{ip}`. Confirm inbound traffic to the reserved address reaches Caddy with no in-guest configuration. |
| Vultr | `POST /instances/{id}/halt` (no separate forced-off endpoint), `POST /snapshots` with `instance_id`, poll status, create instance with `snapshot_id` (not `os_id`). Ownership labels are encoded in the snapshot description. Reserved IPs: `POST /reserved-ips/convert` (keep current IPv4), create instance with `reserved_ipv4`, `DELETE /reserved-ips/{id}`. |

Until retained-address support is implemented, restore requires a null `retainedAddressId`. For DigitalOcean and Vultr, qualification must also verify the provider preserves encoded ownership metadata at production description and label lengths.

Each provider needs fake-HTTP tests in `modules/tests/Selfrun.Worker.Tests/Providers` using `TestHttpMessageHandler`. Live qualification must confirm, per provider:

1. Cloud-init runs per-instance modules on a server created from the snapshot, regenerates SSH host keys, and runs the enrollment script.
2. Services stay stopped on boot until started.
3. `retained`: the retained address survives server deletion and serves traffic on the restored server. For Hetzner and Vultr, it is the same address the environment had before opt-in. `dynamic`: managed records are removed at sleep and resolve to the new address after wake within the managed TTL.
4. Snapshot and restore times and sizes for a representative environment.
5. The billing behavior noted in user docs (snapshot storage, unassigned addresses).

Set `SupportsSnapshots = true` only for providers that pass the snapshot checks, and `SupportsRetainedAddresses = true` only for providers that also pass the retained-address checks. A provider with snapshots but no qualified retained addresses supports sleep in `dynamic` mode only.

## Data model

Names may follow repository conventions. Responsibilities must stay separate.

- `environment_sleep_settings` (1:1 with environment): `sleep_after_minutes`, `http_traffic_is_activity` (default `true`), `address_mode` (derived and cached; recomputed on domain binding changes), `revision`, `power_state`, `state_changed_at`, `last_activity_at`, `last_activity_kind`, `last_http_request_at`, `keep_awake_until`, `last_error_code`, `last_error_message`.
- `environment_power_operations`: `id`, `organization_id`, `environment_id`, `kind` (`sleep`/`wake`), `trigger`, `actor_json`, `status`, `step`, `ownership_token`, `lease_expires_at`, `attempt_count`, `error_code`, `error_message`, `created_at`, `updated_at`, `completed_at`, `deployment_id` (for deployment-triggered wake). Unique partial index: one non-terminal operation per environment.
- `environment_snapshots`: `id`, `environment_id`, `organization_id`, `provider`, `provider_account_id`, `provider_image_id`, `region`, `datacenter`, `sku`, `server_name`, `size_gb`, `status` (`creating`, `pending`, `current`, `superseded`, `deleting`, `deleted`, `failed`), `placement_json` (volume IDs, SSH key references), `created_at`, `deleted_at`. Unique partial index: at most one `current` snapshot per environment.
- `environment_retained_addresses` (at most one live row per environment): `id`, `environment_id`, `organization_id`, `provider`, `provider_account_id`, `provider_address_id`, `ipv4`, `region`, `datacenter`, `previous_ipv4`, `state` (`assigned`, `unassigned`, `releasing`, `released`, `failed`), `created_at`, `released_at`. Unique on (`provider_account_id`, `provider_address_id`) and on live `ipv4` per account.
- `ServerInstance.Status` gains `retired`. All consumers that read "the environment's server" select the single non-retired record. Audit every `db.ServerInstances` consumer: `FindExistingServerAsync`, `SaveServerInstanceAsync`, `EnsureNewAppAsync`, `DomainBindingService`, `CustomDomainVerificationService`, `DomainDnsReconciler`, `DatabaseBackupProcessor`, `DatabaseOperationProcessor`, `DatabaseNetworkProcessor`, `DatabaseMigrationProcessor`, `DatabasesController`, `ApplicationsController`, `DeploymentsController`, `RuntimeController`, `AppDeletionService/Processor`, `AppSummaryReader`, `SshHostKeyTrust`, provider account/catalog services.

The migration is additive. Existing environments get `power_state = awake` and a disabled policy. The address-mode cache is unset until derived from the domain configuration. Snapshot and retained-address records remain keyed to the environment ID after environment deletion so cleanup can retry; their provider account references are organization-scoped.

## Workers

- **Power worker**: claims power operations (`FOR UPDATE … SKIP LOCKED`, maintenance-authority aware like the deployment queue), heartbeats its lease, fences with its ownership token, and runs the sleep or wake procedure as resumable steps.
- **Idle sweep**: runs every minute. It finds due environments and inserts `idle` sleep operations idempotently. The claim repeats every check under the row lock.
- **Snapshot and address reconciler**: runs hourly. It retries purging superseded snapshots and releasing addresses of deleted environments, and reports orphaned Selfrun-labeled images and addresses.

All three register as maintenance participants so maintenance can fence and drain them.

## Implementation breakdown

Proposed native sub-issues under one coordinator issue. Dependencies are explicit; items without dependencies can run in parallel.

| # | Deliverable | Ownership | Depends on | Effort |
| --- | --- | --- | --- | --- |
| 1 | Provider snapshot/power capability + Hetzner, DigitalOcean, Vultr clients and fake-HTTP tests | `modules/common/Providers/**`, provider tests | — | Medium |
| 2 | Data model + migration (settings, operations, snapshots, retained addresses, `retired` servers) and `ServerInstance` consumer audit | `modules/common/Models`, `Migrations`, consumer services | — | Medium / migration |
| 3 | Provider retained-address capability + Hetzner Primary IP, Vultr reserved IP conversion, DigitalOcean reserved IP, fake-HTTP tests (needed for `retained` mode only) | `modules/common/Providers/**`, provider tests | — | Medium |
| 4 | API: policy PATCH (incl. traffic setting, 2h default), address-mode derivation, reservation on enable or first custom domain, DigitalOcean re-point tracking, wake/sleep endpoints, environment DTO, eligibility, `environment_asleep` guards, deletion of snapshots and addresses | `modules/api`, `modules/contracts`, `modules/common/Services` | 2, 3 | Large |
| 5 | Power worker: sleep procedure (new snapshot → purge old → remove managed DNS or keep address → delete server) with rollback before commit | `modules/worker/Power/**` | 1, 2, 3 | Large |
| 6 | Power worker: wake procedure (retained address or managed DNS publish), deployment-triggered wake, deployment claim exclusion | `modules/worker/Power/**`, `Queue`, deployment admission | 1, 2, 3, 5 | Large |
| 7 | Idle sweep, activity recording, database/DNS/domain workers skipping asleep environments, reconciler | `modules/worker`, `modules/common/Services` | 2, 4 | Medium |
| 8 | Runtime agent per-host HTTP activity signal and ingestion into `last_http_request_at` | `modules/runtime-agent`, runtime ingestion | 2 | Medium |
| 9 | CLI commands, help, and `docs/cli.md` | `modules/cli`, docs | 4 | Small–Medium |
| 10 | Dashboard settings, Overview status/actions, selector text | `modules/dashboard` | 4 | Medium / visual verification |
| 11 | Live provider qualification and capability enablement | provider descriptors, docs | 1, 3, 5, 6, 8 | Live operations |

## Acceptance

| ID | Required result |
| --- | --- |
| S1 | Policy (timeout and traffic setting) can be enabled, changed, and disabled from the dashboard, API, and CLI with bounds validation, revision checks, authorization, and persistence across restarts. Enabling without a value stores 2 hours. Traffic counting defaults to on. Ineligible environments are rejected with the specific reason. |
| S2 | With a fake clock, an eligible environment sleeps once `sleepAfter` elapses after its last activity, and not before. Activity, policy disable, or a queued deployment before the commit point cancels the sleep and leaves services running. |
| S2a | With traffic counting on, requests to the environment's hostnames keep it awake. Requests to the bare IP, unknown hosts, and Selfrun health probes do not. With it off, the same requests do not delay sleep. Toggling takes effect without waiting for new requests. |
| S3 | Sleep never deletes a server without a verified new snapshot. Snapshot failure restores a running, awake environment and keeps the previous snapshot. |
| S3a | After each successful sleep exactly one snapshot exists for the environment; the previous one is purged before the server is deleted, or retried by the reconciler with a visible warning. After wake the snapshot is still present. |
| S4 | After sleep, the provider server is deleted, durable volumes are retained and detached, and the `ServerInstance` is retired. In `dynamic` mode the managed A records are deleted before the server, and a failed record deletion leaves the server powered off with `sleep_failed`. In `retained` mode the address is retained and detached and DNS is unchanged. Database backups, domain verification, and DNS reconciliation do not report errors for the asleep environment, and the reconciler does not recreate removed records. |
| S5 | `selfrun environments wake`, the dashboard Wake button, and `selfrun deploy` each restore the environment. The new server has a newly enrolled host key. In `retained` mode it has the same public IPv4, and managed hostnames and verified custom domains serve it without DNS changes. In `dynamic` mode managed hostnames resolve to its new IPv4 before the wake completes. It has the same disk data (SQLite and app-hosted PostgreSQL rows written before sleep), re-attached volume data, and running services passing health checks. |
| S6 | A deployment to an asleep environment wakes it, then deploys the new release, in one CLI invocation and in the GitHub Actions workflow. |
| S7 | Interrupted sleep or wake (worker killed at each step) resumes or fails safely: no duplicate servers, no deleted snapshot without a replacement, no environment left without both a server and a snapshot. |
| S8 | Only one power operation or deployment runs per environment; concurrent wake requests join one operation. |
| S9 | Deleting an asleep environment or its application deletes its snapshot and releases its retained address, or lists them as retained billable resources with a retry. |
| S10 | Internal environments can never sleep. An environment with only managed hostnames is `dynamic` and reserves nothing. Adding a custom domain (or disabling managed DNS) makes it `retained` and reserves the address before the binding is created; removing it keeps the reservation. Hetzner and Vultr reservation keeps the existing address. DigitalOcean opt-in moves managed records to the reserved address and blocks sleep until verified custom domains are re-pointed. |
| S11 | Dashboard shows awake/sleeping/asleep/waking/failed states accessibly at 320–390 px and desktop widths. Runtime observations of an asleep environment read as unavailable, not unhealthy. |
| S12 | Each enabled provider passed live qualification of the provider checks listed above, with evidence on the qualification issue. |

Verification: focused .NET provider, worker, API, and CLI tests; PostgreSQL migration and concurrency tests; dashboard tests and build; browser checks of the settings and Overview flows; and a live sleep/wake cycle on a staging environment for each qualified provider. Follow `README.md` for local checks.

## Decisions

Recorded 2026-11-28:

1. **Stable addresses only where needed (v1).** Environments whose hostnames are all Selfrun-managed Cloudflare DNS get a new address on wake and their records are updated. Environments with custom domains or manual DNS keep one provider-reserved IPv4 address. See [Addresses](#addresses).
2. **HTTP traffic counting is a per-environment setting** next to the timeout, default on. The runtime agent signal (issue 8) ships in v1.
3. **One snapshot at a time, kept after wake.** Each sleep creates and verifies a new snapshot, purges the previous one, then deletes the server.
4. **Default timeout is 2 hours.**

Issues 1 and 3 both edit provider clients. Run them sequentially or split ownership by file to avoid conflicts.

## Future work

- Wake-on-request: while asleep, attach the retained address to a shared lightweight waker that serves a "waking" page and triggers wake on the first request.
- Scheduled sleep and wake windows.
- Organization default policy for new preview environments.
- Retaining IPv6 addresses.
- Connecting a user's own Cloudflare account so custom domains can also use `dynamic` mode.
