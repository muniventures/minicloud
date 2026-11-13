# Stateless control-plane seed

Issue #90 supersedes the required laptop journal and hidden bootstrap identity.
This is source functionality, **not publication or live-provider qualification**.
The [seed release/qualification gate](seed.md) still applies: an exact signed,
compatible server release is mandatory in the default mode. Never bypass compatibility, publish
artifacts, or substitute an unqualified release just to make seed proceed.

## Explicit local source alternative

For operator-controlled TEST deployments without publishing to GitHub or GHCR,
package a **clean, locally committed** source revision:

```sh
umask 077
python3 -B scripts/self-hosting/local_source.py package \
  --version 0.0.1-local.1 \
  --output "$PWD/.local/artifacts/local-seed/source.tar.gz"
```

Build the CLI from that same revision (including its full informational source
SHA). Set `serverVersion` in the protected seed config to `server-v0.0.1-local.1`.
Add `--local-source /absolute/path/source.tar.gz --source-sha256 REVIEWED_SHA256`
to **both** `seed plan` and `seed apply`. Review the archive hash, source, native
architecture, SSH identity and quote before approving. These flags are never
inferred from a failed download; omitting either fails. `check` and `export`
require neither artifacts nor provider credentials for an explicit trusted host.

This mode uses **operator-pinned source**, not GitHub/Sigstore release authority.
The SHA256 must come from the operator's reviewed packaging output, not an
untrusted download. The CLI validates and copies the archive before provider
creation, binds its hash and authority into approval, transfers it through
independently pinned SSH, and verifies both archive and runner hashes on-host
before execution. Never use a checksum beside an untrusted archive as approval.

The dedicated Ubuntu VPS builds the three product images natively, records
their immutable Docker content IDs, validates both worker-agent executables,
and pulls dependencies at the existing pinned registry digests anonymously.
There is **no** product-image push, GitHub release, or GitHub credential on the
VPS. Internet access to upstream package/base-image/dependency registries is
still required. Builds need additional CPU, disk, time and memory; existing
runtime refusal floors are not measured build-capacity guarantees.

The shared installer preserves installation identities, protected credentials,
independent recovery output and maintenance locking. Local mode explicitly
allows initialization/startup before DNS, without replacing HTTPS with HTTP or
exposing internal service ports. A running stack awaiting DNS is **not ready**:
public HTTPS and browser setup remain separate handoffs. Preserve the same
source archive/hash, user key and target across retries; build failures never
authorize replacement VPS creation. Protected build logs are under the uploaded
source's `.local/artifacts/local-seed/`. Local assets are not compatible with
the signed-release maintenance entrypoint; migration to a published release
needs a separately designed and qualified transition, not reinstall/adoption.

## Protected configuration and user-owned key

Create a caller-owned `seedId` **once** with `minicloud seed new-id`
(`generate-id` is an alias). Keep its 32 lowercase hex characters across retries,
machines and CI runs. No command silently generates a replacement identity.
Configuration is JSON schema version 1:

```json
{
  "schemaVersion": 1,
  "seedId": "0123456789abcdef0123456789abcdef",
  "provider": "digitalocean",
  "providerAccount": "DigitalOcean account YOUR-ACCOUNT-UUID",
  "region": "nyc3",
  "size": "s-2vcpu-2gb",
  "domain": "cloud.yourdomain.com",
  "appDomain": "apps.yourdomain.com",
  "serverVersion": "server-v1.2.3",
  "architecture": "linux/amd64"
}
```

Replace example account, selection, domains and release with your reviewed
values. `providerAccount` is the stable authorized account scope, never a token
or token hash: DigitalOcean uses `DigitalOcean account <uuid>`; Vultr uses
`Vultr account <lowercase SHA256 of account email>`. Hetzner exposes no stable
project-identity endpoint: use `hetzner-project:<your independently verified
project identifier>`. That label is a caller assertion, **not** API proof of
project identity. Independently verify the project in its authenticated console,
especially for initial creation or rotated tokens. Existing resources must also
match API-visible ID/labels/region/size/image and independently pinned SSH identity.
Tags aid lookup; possessing a seed ID or resource ID is never authorization.

`architecture` defaults to `linux/amd64`; `linux/arm64` is supported when the
selected image/plan actually matches. SSH user is currently `root` only.
Optional `operatorPublicKeys` is an array of canonical OpenSSH public keys,
installed only during partial-host continuation. Region, size, image, account,
domains and preparation identity are not a desired-state rebuild instruction.

Keep config and credential/key files owner-only (0600 or 0400), containing
directories 0700, without symlinks or group/world-writable ancestors. The
separate `--credential-file` overrides optional protected `providerCredential`;
neither is required for SSH continuation/check/export of an explicit target.
Provider credentials are never sent to the VPS.

Always supply the **actual user-owned** private key with `--ssh-key`. Its public
key, not a second hidden automation identity, is registered during new creation.
Its `.pub` sidecar is optional; the key is never copied into CLI configuration,
overwritten or exported in the public report. OpenSSH ed25519 and RSA >=3072-bit
keys are supported.
An existing provider key with the same public bytes is reused without modifying
or deleting it; ambiguous matches require reconciliation. Otherwise only the
supplied user key's public half is registered, never a hidden second key.

For an encrypted key, independently unlock it with your normal OpenSSH agent,
then opt in with `--use-ssh-agent`. Only that option preserves `SSH_AUTH_SOCK`;
the agent must contain the matching identity. Agent forwarding is always off,
and SSH config, password/keyboard-interactive authentication and arbitrary
forwardings are disabled. Minicloud never takes a passphrase in arguments/logs
or asks you to remove one.

Optional explicit key generation during `plan`:

```sh
minicloud seed plan --config seed-config.json --report reports/seed-report.json \
  --generate-ssh-key keys/my-seed-key
```

The existing interactive **“Protect ... with a passphrase? [Y/n]”** question is
preserved (yes by default). `ssh-keygen` handles hidden passphrase entry itself.
Existing private/public paths are never overwritten. Load a newly encrypted key
into your agent and rerun plan with `--ssh-key keys/my-seed-key --use-ssh-agent`;
generation itself grants no creation authority. Noninteractive generation needs
an explicit `--no-ssh-key-passphrase` choice; protected generation needs a terminal.
CI must supply an unencrypted protected key or explicitly arrange unattended
agent unlocking outside Minicloud. The reusable workflow does not provide an agent.

## Initial creation, and the pre-host uncertainty limit

Create a protected `reports/` directory, then review a read-only plan:

```sh
minicloud seed plan --config seed-config.json --report reports/seed-report.json \
  --ssh-key keys/my-seed-key --credential-file provider-token --new-seed
minicloud seed apply --config seed-config.json --report reports/seed-report.json \
  --ssh-key keys/my-seed-key --credential-file provider-token --new-seed \
  --approve EXACT_REVIEWED_DIGEST
```

For initial creation, config must additionally contain
`"initialCreationIntent": "one-authorized-creator-no-unresolved-create"`.
Together with `--new-seed`, this is your explicit assertion **for this dispatch**
that there is exactly one authorized creator and no unresolved prior request.
Review selection, costs/exclusions, exact signed release, account scope, SSH key,
classification and resolved resource in the plan/report. Approval binds all of
these, including initial intent and independent fingerprint, but not credential
bytes or quote retrieval time. Rotated authorized credentials are allowed.

There is **no distributed exactly-once guarantee**. The supported provider create
APIs here have no relied-upon idempotency key. Labels and inventory reads are not
atomic cross-machine locks; zero inventory results do not prove a pending create
failed. Requests are dispatched once, without an automatic retry or rollback.
An existing operation SSH key also blocks initial creation: it may be evidence
of an interrupted request. **After a timeout, lost response, cancellation or
uncertain resource/key creation, do not repeat the initial-create assertion.**
Keep the same seed ID and reconcile authenticated provider console/support,
pending operations and billing, including eventual inventory visibility.
Never “fix” uncertainty by deleting a report/key or changing the seed ID.
Only after independent reconciliation can a human review a new initial attempt.
When reusing a pre-existing user key, no new operation-key object exists to help
detect uncertainty; the same one-creator and explicit reconciliation rules apply.

An accepted create intentionally pauses for an independent trust handoff.
Copy the returned target/resource ID and IPv4 into protected config, obtain the
ed25519 fingerprint through the provider's authenticated console, and add:

```json
"target": {
  "serverId": "PROVIDER-RESOURCE-ID",
  "ipv4": "203.0.113.10",
  "hostFingerprint": "SHA256:INDEPENDENTLY_OBTAINED_43_CHARACTER_BASE64_VALUE"
}
```

The example fingerprint is a placeholder, not a valid pin. Include target
`image` from the creation report/console before initial host-only preparation
without a provider credential; authenticated inventory can otherwise resolve it.
Optional `primaryIpId` further constrains provider lookup. Never substitute
an IP or domain for resource/seed identity. Remove the initial creation intent,
omit `--new-seed`, replan, and approve **that new continuation plan**. Missing,
stale or edited reports never authorize creation.

## Machine B, continuation and observational check

On a fresh machine/runner, bring only the explicit protected config and your
separate user-held key (plus an explicitly unlocked agent if needed):

```sh
minicloud seed check --config seed-config.json --report reports/seed-report.json \
  --ssh-key keys/my-seed-key
minicloud seed plan --config seed-config.json --report reports/seed-report.json \
  --ssh-key keys/my-seed-key
minicloud seed apply --config seed-config.json --report reports/seed-report.json \
  --ssh-key keys/my-seed-key --approve EXACT_REVIEWED_DIGEST
```

No persistent CLI home, local journal, machine lock, original credential bytes or
backup acknowledgement is required. A provider credential is optional for an
explicit target; if supplied, authenticated read-only inventory must agree.
Zero/one/multiple matching labels resolve respectively to absent/that
resource/reconciliation, never silent replacement. Duplicate matches fail closed.

| Classification | Meaning/action |
| --- | --- |
| `new_seed` | Inventory absence plus explicit initial intent; creation still needs reviewed approval and the one-creator contract. |
| `continue_partial` | Continue this original dedicated host/preparation/installer only. Preserve installer installation/operation IDs and resource IDs. |
| `current` | Requested release already installed. No seed preparation/installer/service action; observe health and pending browser/DNS/recovery handoffs. |
| `upgrade_required` | Installed identity/release discovered first, then a different supported signed release requested. Use existing signed coordinated maintenance, not seed. |
| `reconciliation_required` | Unknown/corrupt/missing identity on an occupied host, conflicting immutable config, unknown schema, uncertain access or incomplete maintenance. Stop. |

`check` does **not** invoke `InstallAsync(statusOnly)`, installer `status`, Compose,
bootstrap, downloads/uploads, service lifecycle, token rotation, repair or unseal.
It reads protected `installation.json`, the original seed operation binding and
preparation checkpoint, and maintenance metadata. It observes Docker container
state with `ps`/`inspect` (never Compose) and verifies ordinary public HTTPS/DNS.
It takes a shared **existing** installer-lock inode where present; it never
creates a host lock or metadata, even on partial/failing hosts. Ordinary SSH/HTTP
access logging is not a forbidden mutation.

Host mutations use `/opt/minicloud.installation.lock`, the same persistent inode
as the signed installer/maintenance. Preparation/checkpoints use protected,
crash-safe host writes. Future schemas, occupied hosts without metadata,
interrupted checkpoint writes, active locks and pending upgrade/restore/fence
records fail closed. No second installer history is introduced.
Uploads/key additions revalidate observations while holding that lock. The
updated signed installer rejects incomplete maintenance under its own lock
before normal installation/readiness actions; this safeguard must be present in
the selected compatible release, not retrofitted by executing unsigned host code.

Without `--ssh-key`, `check` performs public HTTPS observation only, without any
provider token when a target is explicit. Protected host identity, preparation,
OpenBao and recovery custody remain unknown, so public HTTPS alone cannot claim
the entire seed is ready. A previously ready metadata phase likewise does not
prove live readiness. Unhealthy OpenBao after a reboot is unavailable, **not**
permission to unseal/reinitialize it automatically.

Different installed/requested releases do not trigger reinstall. An upgrade
needs separate explicit approval and the existing signed maintenance flow,
verified previous assets, backup inputs, compatibility and fencing/drain.
Seed does not implement upgrades, domain migrations, rollback or destructive cleanup.

## Portable report and private handoff

`--report` writes schemaVersion 1, kind `minicloud-seed-report`: a regenerable
non-secret snapshot with seed/resource IDs, target IPv4/root SSH user, independently
verified fingerprint when actually checked, domains/DNS instructions, exact
requested/observed release/source, approval/plan/costs, observed progress and
health, classification/reasons/actions and known retained billable resources.
Unknown, pending, unavailable and failed remain distinct. A lost create response
can leave resource IDs unknown; the report explicitly requires console/support
reconciliation rather than claiming there are no billable resources.

The report is **not state or authority**. Existing output is replaced atomically
only when it is a protected supported seed report, never a key/journal/unrelated
file. No API credentials, private keys, setup token, OpenBao shares or recovery
secrets belong in it. Exceptions and host/provider diagnostics are suppressed.
Private files use a separate sibling directory, not the report artifact tree:

```sh
minicloud seed export --config seed-config.json --report reports/seed-report.json \
  --ssh-key keys/my-seed-key --recovery-dir private-recovery --setup-token
```

Export reads the existing protected installer outputs without generating or
rotating anything. Omit `--setup-token` unless needed. It preserves conflicting
existing files instead of overwriting them and requires original installed
identity/release. Independently encrypt/back up and verify recovery shares,
root-token/CA-key material and any backup-encryption key. Enter any setup token
manually at verified HTTPS `/setup`, never in a URL or report. Exports are not
uploaded by the seed workflow. Missing recovery custody cannot be inferred as
successful from a ready server.

Stateless exits match the reusable workflow: **0** successful plan/export or
fully observed ready host; **10** handoff pending; **11** upgrade required;
**12** reconciliation required; **2** malformed invocation before a report can be
established. Check/apply never turn stale success into live ready.
An interrupted export may resume into the same protected directory from a
fresh machine: existing original bytes are compared, never replaced.
The workflow reference accepts the same flat schema and
`--config/--report/--ssh-key/--credential-file/--new-seed/--approve` arguments.
Its config secret must include the explicit `providerAccount` described above
and initialCreationIntent only for a reconciled initial attempt; no wrapper
code change or workflow dependency is required.

## Lost access and independent recovery

- **Hetzner:** use the authenticated Cloud Console console/rescue options,
  verify the project/server ID, and install your replacement authorized public
  key through supported rescue/recovery procedures.
- **DigitalOcean:** use its authenticated Recovery Console/recovery ISO or root
  password reset as appropriate for the droplet.
- **Vultr:** use authenticated View Console/recovery ISO/root-password recovery
  options supported by that instance/OS.

Follow current provider documentation; availability varies by account/image.
Preparation disables password SSH. A provider root-password reset **may not
enable password SSH**: use console/rescue to restore `authorized_keys`, rather
than weakening SSH blindly. Recovering root access does not establish host
identity: independently reverify fingerprints and resource IDs after a rebuild
or change. Never auto-replace a trust pin or rebuild because access failed.
Root does not recover lost encrypted-backup keys or OpenBao recovery shares.
Rebooted sealed OpenBao may need the operator's independent shares/unseal flow;
missing shares are not solved by reset, reinitialization, escrow or key rotation.

## Legacy transition and validation

The original `seed --provider ...`, `status OP`, `resume OP`, `export OP`,
`cleanup OP` and optional registration interfaces remain for existing callers.
Their user-owned keys/journals are preserved, not silently imported, deleted or
converted. For stateless access, explicitly select a user-held key already
authorized on that host and original seed/resource identity and independent pin.
No automatic secret discovery/copy from legacy CLI directories occurs.
The stateless path has no destructive cleanup command.

Run the [protected-worktree seed tests](seed.md#local-seed-tests); the seed-filtered
.NET suite also runs the Python observational inspector fixture tests on Unix.
These tests use synthetic providers/hosts and do not qualify real provisioning.
