# Server maintenance: backup, upgrade, restore and transfer

No server release is published or qualified yet, and these flows have not been
qualified on a real host. They are built on the durable
[maintenance authority](server-maintenance-authority.md), which runs inside the
API image as
`dotnet Minicloud.Api.dll maintenance <status|fence|drain-status|release|transfer>`.
Maintenance **never stops, kills or cancels accepted work**. It fences new
management writes, waits a bounded time for accepted work to drain, and gives up
(reopening writes) if the work does not drain.

## Verified entry point

Follow the [installer trust procedure](server-installer.md). Use the original
root, configuration, independent recovery directory and public addresses. After
you verify the signature independently, run:

```sh
sh "$assets/install-server.sh" --tag "$tag" --assets-dir "$assets" \
  maintenance status --root "$root" --config "$config" \
  --recovery-dir "$recovery" --public-address "$address"
```

`maintenance.sh` in the verified bundle forwards to this same installer. Most
operations run the maintenance code from the **installed** bundle, and only
after the signed release and the whole installed inventory pass validation.
`upgrade` and `restore` instead run the signed helper from the **selected**
release assets, checked against that release's digest. Every operation holds
the persistent `<root>.installation.lock`; never remove that lock file.

The tooling calls the authority with
`docker compose run --rm -T --no-deps api-bootstrap maintenance <op> --stdin`.
Identifiers travel as JSON on stdin, never on the command line. The installation
keeps a host identity in `<root>/host-id` (protected, never backed up). Compose
passes it to the API and worker as `Maintenance__HostId`.

The local record is `<root>/maintenance.json`. It holds the last operation and
its state (`started`, `failed` or the result), the held fence, any pending
upgrade or restore, and the last failure with its rollback guidance. An
interrupted operation stays `started`; it is never reported as a success.

`status` reports one of these states, together with the authority's own status:

- `maintenance_idle`
- `fenced`
- `restore_pending_confirmation`
- `maintenance_incomplete`
- `authority_unavailable`

The installer exits 0 when the operation finishes in a final success state.
It exits 2 when the result needs follow-up, for example `restored_fenced`,
`fenced` or `authority_unavailable`. Errors exit nonzero with a sanitized
message.

`stop` and `remove` are not maintenance operations. They always refuse.
Persistent volumes and customer servers are never removed by this tooling.

## Key files

Install `age` and `age-keygen` independently. Use the official distribution or
your OS package repository (for example `apt-get install age` on Ubuntu 24.04).
Create an age identity file and its matching recipients file with `age-keygen`.
Keep the identity escrowed **off-host** as well.

The full backup flow authenticates every captured component with the identity
before it reports success, so a copy of the identity must be readable on the
host while the backup runs. Commands accept protected **file paths**, never key
values. Never put key material in shell history, arguments, the environment or
a URL.

Use canonical absolute paths:

- Every ancestor must be root-owned and not group- or world-writable.
- Directories must be `0700`.
- Files must be `0600` or `0400`.

Keep backups, recipients and identity outside the installation root, the assets
directory and the recovery directory. The identity must also be outside the
backup directory.

## Coordinated operations

Append these operations and options to the common command above, in place of
`status`. "Capture args" below means:
`--backup-dir "$backups" --recipients-file "$recipients" --identity-file "$identity"`.

| Operation | Additional arguments | Effect |
| --- | --- | --- |
| `backup` | Capture args, optionally `--drain-timeout SECONDS` (default 1800) | Fence, drain, capture, verify and release. |
| `verify-backup` | `--archive "$backups/<id>.backup" --identity-file "$identity"` | Authenticate and digest-check every component without writing plaintext. |
| `remove-backup` | Verify args plus `--confirm-backup-id <id>` | Delete only that authenticated `.backup` directory. |
| `schedule-backup` | Capture args plus `--output-dir "$units" --at HH:MM` | Write `minicloud-backup.service` and `.timer` for a daily UTC backup. Units are never installed, started or enabled. |
| `upgrade` | Capture args plus `--previous-assets-dir "$old_assets"` | Run the coordinated upgrade to the selected `--tag`. |
| `restore` | `--archive "$backups/<id>.backup" --identity-file "$identity"`, optionally `--confirm-replace-live <installationId>` | Restore the backup and leave writes **fenced**. |
| `restore-confirm` | `--confirm-operation-id <restore operation>` | Check that services are ready, then reopen writes. |
| `transfer` | Optionally `--keep-fenced` | Move the authority to this host and restart the API and worker. |
| `release` | `--confirm-operation-id <holder>` | Escape hatch: release a fence that this tooling recorded. Refuses the operation of a pending restore or upgrade. |

### Backup

A backup runs in this order:

1. **Fence.** The authority refuses new management writes.
2. **Drain.** Poll `drain-status` every 10 s until it reports `drained`.
   - If the work does not drain within `--drain-timeout`, the backup gives up.
   - If uncertain work persists for 60 s, the backup refuses.
   - If the fence is lost, the backup stops.

   In every case the fence is released and no backup is published. Reconcile
   uncertain work explicitly, then retry.
3. **Capture.** Each component is streamed through `age` into
   `<backupId>.partial/` as a separate `.age` file:
   - `pg_dump --format custom` of the `minicloud` and `kratos` databases, taken
     with `docker compose exec`.
   - Read-only `tar` of the `openbao`, `artifacts` (including registry data),
     `caddy-data` and `caddy-config` volumes, using `--network none` helper
     containers. OpenBao is paused (not stopped, so it stays unsealed) while
     its file storage is copied.
   - `installation.json` and `secrets/`.
   - The independent OpenBao recovery material.
   - An encrypted manifest with the release, authority epoch and component
     digests.

   The capture needs at least 2 GiB free at the destination. Nothing is pruned
   automatically.
4. **Verify.** Decrypt every component with the identity and check it against
   the manifest. Only then is the directory renamed to `<backupId>.backup`.
5. **Release** the fence.

If any step fails after the fence is taken, the tooling deletes the partial
directory and releases the fence. Kratos sessions and logins are not
management writes, so they can continue during the short capture window.

### Upgrade

Keep the **previous** release's verified assets directory. Pass it as
`--previous-assets-dir`. Its bundle digest must equal the installed
`bundleSha256`.

The upgrade refuses before changing anything if any of these hold:

- The target is a downgrade.
- The target is the same release that is already installed.
- The installed release predates the `MaintenanceAuthority` migration.

Otherwise it pulls the verified target images and then advances through
persisted phases:

1. Fence and drain, as for a backup.
2. Take a **pre-upgrade backup**.
3. Stop worker, API, dashboard and Kratos.
4. Move the old bundle to `bundle-previous-<operation>` and extract the new one.
5. Start PostgreSQL and OpenBao, then unseal OpenBao.
6. Run the `api-migrate` and `kratos-migrate` migrations.
7. Start services.
8. Verify container health and HTTPS readiness.
9. Release the fence.

On failure, recovery depends on how far the upgrade got:

- **Before migration.** The previous bundle and release metadata are put back
  and the old services are restarted. Writes reopen only after they report
  healthy. If that rollback fails, writes stay fenced.
- **During or after migration.** Writes **stay fenced**. `lastFailure.rollback`
  in `status` gives the operation ID and the pre-upgrade backup. Fix the cause
  and re-run the same upgrade to resume from the recorded phase. Alternatively,
  roll back by running the **previous** release's installer
  `maintenance restore` on the pre-upgrade backup. Never downgrade images alone.

Re-author scheduled units after an upgrade, because they pin the old release's
assets.

### Restore

`restore` uses the **exact** release that created the backup. It requires the
original root path and the original configuration file. It decrypts and checks
every component **before** it changes anything. A tampered, truncated or
wrong-identity backup is rejected with nothing changed.

A host may still be serving writes for this installation. That is the case when
the authority is `open`, or unknown because the installed release differs or
the database is unreachable. Restore then refuses unless you pass
`--confirm-replace-live <installationId>`. With that confirmation, an open
authority is fenced and drained first. Restore refuses outright to replace a
different installation at the same root.

Restore then runs these steps:

1. Stop this project's containers. A fresh host gets tooling checks and image
   pulls instead.
2. Write `installation.json` and `secrets/`. Differing current secrets or
   bundles are moved aside as `*-superseded-<operation>`, never deleted.
3. Write the recovery files and restore the volume contents.
4. Restore both databases with `pg_restore --clean --create`.
5. Unseal OpenBao and run migrations.
6. **Transfer** the authority to this host under a new epoch.
7. Start services.

The installation **stays fenced**. Verify it, then run `restore-confirm` with
the printed operation ID to reopen writes. A failed restore can be repeated
with the same archive. Services stay stopped or fenced until it succeeds.

### Transfer and its limitation

`transfer` binds the authority to this host's `host-id` and increments the
epoch. If this host currently holds an authorized open authority, transfer
fences and drains first. It then recreates the API and worker so they register
under the new epoch, checks readiness, and releases the fence unless you pass
`--keep-fenced`.

**Limitation.** Transfer revokes old processes only when they next reach
**this** database. Transfer does **not** stop either of these:

- An old host running on its own restored or copied database.
- An unreachable old host that still holds live provider, DNS or SSH
  credentials.

Shut the old host down, or rotate those credentials, before relying on the new
installation. The command prints this limitation every time.

## Independent recovery-dependency archives

These are **not coordinated control-plane backups**; use `backup` for that. They contain only the
original OpenBao CA private key, original recovery response/unseal shares and
root credential from `$recovery/<installationId>/`. They do **not** contain
the application or identity databases, OpenBao persisted state, application
artifacts, installation secrets/SSH keys or TLS volumes. Customer database
backups are separate and do not fill these gaps.

They use the same [key files](#key-files) and path rules. Rehearsal output
must also stay outside those namespaces. The path guards do not create an
off-host copy.

| Operation | Additional arguments | Effect |
| --- | --- | --- |
| `backup-recovery` | `--backup-dir "$backups" --recipients-file "$recipients" --identity-file "$identity"` | Encrypt in memory, authenticate/decrypt/validate the complete result, then exclusively publish `<backupId>.recovery.age`. |
| `verify-recovery` | `--archive "$archive" --identity-file "$identity"` | Authenticate and check complete inventory, digests, original IDs, exact source/bundle/release and original CA without writing plaintext. |
| `rehearse-recovery` | Same as verify, plus `--output-dir "$new_directory"` | Finish all checks before creating a new protected directory containing recovery files. Starts no container/worker/network and makes no live provider call. |
| `retention-plan` | `--backup-dir "$backups" --identity-file "$identity" --keep 7` | Authenticate each recovery archive and full `.backup` manifest. List older candidates for each type and any leftover `.partial` captures. Deletes nothing. Unknown files make planning refuse. |
| `remove-recovery` | Verify arguments, plus `--confirm-backup-id "$backupId"` | Delete only that authenticated, correctly named archive. No installation/volume/provider data is deleted. |
| `schedule-recovery` | Capture arguments, plus `--output-dir "$units" --at 03:15` | Write new `minicloud-recovery.service`/`.timer` units for daily UTC capture. Never install/start/enable units. |

Archive verification is intentionally restricted to the same original installed
release and CA. It is not a lost-host full restore command or an upgrade
compatibility declaration. Rehearsal exposes original recovery secrets in the
protected output; independently protect that directory. Delete only your
explicit rehearsal files after verification, never broad installation paths.

The scheduler re-enters the independently verified asset installer and same
lock. Existing unit files are never overwritten. Operator review and manual
installation on a future qualified dedicated host are separate actions.
This timer schedules **recovery dependencies only**, not a full backup; use
`schedule-backup` for full backups.
Inspect maintenance status and the unit's exit result; failed/busy captures do
not become successful backups, and no automatic retention deletion occurs.

## Explicit off-host copy

Copy each successful `<backupId>.backup` directory, and each `.recovery.age`
archive, to independent protected storage over an authenticated channel. Record its backup ID and ciphertext SHA-256,
verify the copied hash, then exercise authenticated decryption/rehearsal with
the original exact-release installation context. Independently escrow the age
identity **separately** from the ciphertext and from the recovery material it
decrypts. Confirm both can be recovered without the original disk. A successful
local capture/hash is not proof of an off-host copy or full disaster recovery.

If everything is kept only on the control-plane VPS, losing that host loses
both the backups and the keys. For a lost host, copy the backup and the
exact-release assets to the new host, then run `restore` there. Read the
[transfer limitation](#transfer-and-its-limitation) first.

Preserve all persistent volumes and release assets. Never run a broad
`docker prune`, never delete customer VPSs, and never use an image downgrade as
a substitute for a coordinated restore.
