# Server maintenance and recovery dependencies

No server release or full recovery path is published/qualified. **T44 remains
blocked:** production lacks a common management-write admission fence, complete
accepted-work drain and independently enforceable old-worker transfer fence.
The [required interfaces](../packaging/server/bundle/upgrade-prerequisites.md)
are mandatory before upgrade, coordinated capture or production recovery.
Never stop accepted provisioning to obtain a backup.

## Verified entry point

Follow the [installer trust procedure](server-installer.md). Use the exact
**installed** release and original root, configuration, independent recovery
directory and public addresses. After independent signature verification:

```sh
sh "$assets/install-server.sh" --tag "$tag" --assets-dir "$assets" \
  maintenance status --root "$root" --config "$config" \
  --recovery-dir "$recovery" --public-address "$address"
```

`maintenance.sh` in the verified bundle forwards to this same installer.
Downloaded/installed maintenance code executes only after the signed release
and entire immutable installed inventory pass validation. Every operation uses
the persistent `<root>.installation.lock`; never remove that lock file.
No cache/output belongs inside the installed bundle.

`status` returns exit 2 with explicit unavailable fence/drain/transfer states
and the last maintenance outcome. `backup`, `upgrade`, `restore`, `transfer`,
`stop` and `remove` return a sanitized nonzero blocker **before any service,
migration, volume, provider or installation-data operation**. They do not
silently disable writes, claim a fence or promise recovery. An interrupted
operation remains `started`, not success; ordinary failures record `failed`.

## Independent recovery-dependency archives only

These are **not coordinated control-plane backups**. They contain only the
original OpenBao CA private key, original recovery response/unseal shares and
root credential from `$recovery/<installationId>/`. They do **not** contain
the application or identity databases, OpenBao persisted state, application
artifacts, installation secrets/SSH keys or TLS volumes. Customer database
backups are separate and do not fill these gaps.

Install `age` and `age-keygen` independently from the official distribution or
your OS package repository. Prepare an independently recoverable age identity
file and matching recipients file using `age-keygen`. Never put secret
decryption material into shell history, arguments, environment, URLs or an
archive. Commands accept protected **file paths**, not key values.

Use canonical absolute paths with root-owned non-writable ancestors, private
`0700` directories and `0600`/`0400` files. Keep archives, recipients,
decryption identity and rehearsal output outside installation, downloaded
assets and independent recovery namespaces. The decryption identity must
also be outside the archive directory. These path guards do not establish an
off-host copy.

Append these operations/options to the common verified command above
(replace `status`):

| Operation | Additional arguments | Effect |
| --- | --- | --- |
| `backup-recovery` | `--backup-dir "$backups" --recipients-file "$recipients" --identity-file "$identity"` | Encrypt in memory, authenticate/decrypt/validate the complete result, then exclusively publish `<backupId>.recovery.age`. |
| `verify-recovery` | `--archive "$archive" --identity-file "$identity"` | Authenticate and check complete inventory, digests, original IDs, exact source/bundle/release and original CA without writing plaintext. |
| `rehearse-recovery` | Same as verify, plus `--output-dir "$new_directory"` | Finish all checks before creating a new protected directory containing recovery files. Starts no container/worker/network and makes no live provider call. |
| `retention-plan` | `--backup-dir "$backups" --identity-file "$identity" --keep 7` | Authenticate every exact-installation/exact-release archive, then list older candidates; deletes nothing. Unknown/mixed files refuse planning. |
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
This timer schedules **recovery dependencies only**, not a full backup.
Inspect maintenance status and the unit's exit result; failed/busy captures do
not become successful backups, and no automatic retention deletion occurs.

## Explicit off-host copy

Copy the exact successful `.recovery.age` archive over an authenticated channel
to independent protected storage. Record its backup ID and ciphertext SHA-256,
verify the copied hash, then exercise authenticated decryption/rehearsal with
the original exact-release installation context. Independently escrow the age
identity **separately** from the ciphertext and from the recovery material it
decrypts. Confirm both can be recovered without the original disk. A successful
local capture/hash is not proof of an off-host copy or full disaster recovery.

Keeping everything only on the control-plane VPS loses both capture and keys
when that host is lost. No command currently provides full lost-host recovery
or production authority transfer. Preserve all persistent volumes and source
release assets; never run broad Docker prune, delete customer VPSs or use an
image downgrade as a substitute for coordinated recovery.
