# Versioned server installer and manual Compose

The implementation exists, but **no server release is published or qualified**.
The commands below describe a future exact signed release, not permission to
publish, install a public host, or replace missing release assets with source
builds. Native amd64/arm64, full-image, real Kratos, Caddy/ACME and hosted
acceptance remain release gates. Upgrade/full recovery tooling belongs to T44.

## Trust and prerequisites

Use a dedicated Ubuntu 24.04 host, `amd64` or `arm64`. Install Python 3,
Cosign **2.6.5**, Docker Engine, Compose and Buildx independently using their
official distributions. OpenSSL and OpenSSH's `ssh-keygen` are required.
T40's verifier already requires Docker/Compose/Buildx; this installer does
not bootstrap, upgrade or replace an engine. Engine 24+ and Compose 2.24+
are mechanical command-interface guards, not a fully qualified version matrix.
The installer uses only the local `/var/run/docker.sock`, not an inherited
remote context. No source checkout, GitHub login, .NET SDK, Node or compilation
is needed on the server.

Allow outbound HTTPS for the exact GitHub release, GHCR/dependency registries,
Sigstore production trust/transparency infrastructure and certificate issuance.
Subsequent provider/API traffic is operator-configured. Allow inbound TCP
80/443 and, for Caddy HTTP/3, UDP 443. SSH and host firewall policy remain
entirely operator-managed; the installer never modifies them.

Select an actual published `server-vMAJOR.MINOR.PATCH[-prerelease]`, not
repository-wide `latest`. Follow the independent
[T40 trust procedure](../packaging/server/README.md#trust-and-exact-release-verification):
download its six named assets into an empty root-owned `0700` directory,
with regular, nonsymlink root-owned `0600`/`0400` files. Its path ancestors
must be root-owned and not group/world writable. Independently run
`cosign verify-blob` with the exact selected tag's workflow identity and
production issuer, then `sha256sum --check --strict checksums.txt`.
Only after that may the downloaded verifier or installer execute.
Never use an unchecked `curl | sh`, adjacent checksums as publisher identity,
wildcard certificate identities, or disabled transparency/inclusion checks.

The installer repeats independent signed-inventory/hash checks before
executing the downloaded verifier and the archive's versioned helper. The
verifier checks the detached manifest, complete safe archive inventory,
runtime manifest core, pinned images and source/workflow provenance.
Image verification is read-only before host preflight. Anonymous image pulls
(both release platforms, then the native variant) happen only after preflight
and before creating installation state, with empty Docker authentication.
No missing/unsigned release is treated as an installable release.

The optional `--download` flag downloads only absent assets from that **exact**
release URL into an already protected assets directory. You must still have
independently verified the installer before using it. Existing files are never
replaced. Failed downloads retain a `.partial` file; inspect and remove only
that exact partial file before retrying. No installer/verifier executes after
a failed download, signature or checksum check.
Promotion uses an atomic no-replace hard link on the assets filesystem: if
another invocation supplies the destination during download, it is preserved,
the completed `.partial` remains for inspection, and this invocation fails.

## Protected inputs and preflight

Prepare non-secret JSON using the release's `configuration.example.json` and
`configuration.schema.json`. Store it outside installation/recovery outputs
as a root-owned regular `0600`/`0400` file, in a root-owned `0700` input
directory. Supply lowercase hostnames and exact HTTPS origins without a port,
path or trailing slash. The API hostname is `api.<dashboard hostname>`.
The app suffix is separate; wildcard app DNS is **not** control-plane DNS.
Unknown/duplicate fields, ambiguous paths and symlinks are rejected.

Choose three distinct canonical absolute paths:

- `$assets`: verified download directory (`0700`).
- `$root`: installation directory, normally `/opt/minicloud`. Its parent
  must already exist and be root-owned, without group/world write access.
  The target must be absent for a fresh install; even an unknown empty
  directory is not silently adopted.
- `$recovery`: existing root-owned `0700` operator recovery directory,
  preferably independently mounted/off-host recovery media, **outside** and
  nonoverlapping with both installation and asset paths. If configured,
  `recovery.openBaoUnsealMaterialPath` must equal this path. A path alone
  does not prove an independent/off-host copy; verify recoverability yourself.
  `backupDestination` is reserved operator information, not configured backup
  automation or a claim that full coordinated backup exists.

Set `$config` to the protected input and `$address` to the host's public IP
address. Repeat `--public-address` for each public IPv4/IPv6 address. Both
hostnames must resolve only to these supplied addresses. Future-release
command template, after independent verification:

```sh
sh "$assets/install-server.sh" --tag "$tag" --assets-dir "$assets" preflight \
  --root "$root" --config "$config" --recovery-dir "$recovery" \
  --public-address "$address"
```

Preflight checks OS/architecture, local daemon/tool interfaces, protected
configuration/paths, conflicting services, TCP 80/443 and UDP 443, DNS,
existing installation identities and installation-owned volumes/networks.
A host with unsupported IPv6, or a kernel-confirmed global IPv6 disablement,
may pass using IPv4 alone. IPv6 TCP/UDP listener conflicts, permission errors
and unconfirmed address errors still fail; the installer never enables IPv6.
A fresh dedicated host must have no containers (including stopped ones) or
Minicloud-prefixed volumes/networks. Recognized resumes may reuse only their
installation-labeled persistent resources and original Compose working path.
Never rename/delete existing populated volumes or edit metadata to bypass
these checks.

Mechanical capacity guards are **2 logical CPUs, 2 GiB physical RAM**, and
free disk on **both installation and Docker storage filesystems** of at least
`max(10 GiB, 3 × compressed release archive bytes)`. The disk rule reserves
archive/extraction headroom plus a fixed image/runtime floor. These are
conservative refusal guards, **not measured supported hardware requirements**;
they do not predict image expansion, app storage or upgrade/backup needs.
T90/T91 must measure the whole stack and publish qualified requirements.
No automatic pruning/cleanup occurs. Missing/mismatched DNS returns
`awaiting_dns_tls` before creating a fresh installation. Public TLS cannot
exist before issuance; it is checked with system certificate validation,
without redirects, during activation/status. There is no insecure admin mode.

## Automatic sequence

Repeat the template with `install` in place of `preflight`. Under the persistent
installation-wide lock, it verifies/pulls, durably records original
`installationId`/`operationId` and exact release metadata, extracts only verified
files, generates independent protected credentials and deployment SSH keys,
then calls the existing Compose database initializer and API/Kratos migrations.
It starts Kratos before shared bootstrap operations.

OpenBao is initialized only once. Its original recovery response (three
unseal shares, threshold two, and root token), separate root-token file and
private CA key are written under `$recovery/<installationId>/` with `0600`.
Unseal keys use protected redirected input, not arguments. Configuration uses
the existing `openbao-configure` helper's protected root-token bind; ordinary
API/worker consumers use only the renewable Agent memory-backed sink.
The root-token file is not mounted into normal services. See the
[OpenBao handoff](../packaging/server/bundle/openbao-handoff.md).
Protect and independently copy recovery material before relying on the host.
An initialized server without original recovery output, or uncertain/missing
AppRole output, fails closed for host reconciliation; it is never reinitialized.

`bootstrap prepare` uses the existing API image and producer, never another
seed algorithm. Its protected request carries the two original stable IDs;
separate read-only input/read-write output directories retain T41's interface.
The one-time token goes only to
`$root/secrets/bootstrap/output/setup-token-initial`, never routine stdout,
environment, URLs or diagnostics. Locally read that protected file and enter
the token at the reported HTTPS `/setup` URL. Follow
[operator setup](operator-setup.md); provider/plan configuration and VPS
allocation are subsequent explicit actions.

## Manual Compose parity

Use the **same independently verified installer asset as a staged host helper**,
changing only its command in the template:

1. `prepare`: all preflight, verification/pulls, durable metadata, extraction
   and protected configuration/credentials; no services started.
2. `initialize`: shared Compose `postgres`, `database-initialize`,
   `api-migrate`, `kratos-migrate`, healthy `kratos`, persistent OpenBao/Agent
   configuration, then shared `api-bootstrap status`/`prepare`.
3. `activate`: shared Compose `api`, `dashboard`, `worker`, then `caddy`,
   followed by the same HTTPS/bootstrap readiness.
4. `status`: same read-only readiness; updates only host readiness metadata.

`install` calls these exact same primitives, not another installation mode.
Each step retains the same config, tag, paths, public addresses and operation
identities and acquires the same persistent lock. The bundle under
`$root/bundle/` is the release's digest-rendered Compose, not source Compose.
The shared helper supplies only validated public values, image references,
`MINICLOUD_INSTALLATION_ID` and protected secret **paths**. Direct Compose
host maintenance must preserve that exact environment and the same lock;
do not bypass it with ad-hoc `docker compose up`, copied templates or parallel
host commands. Existing migration/bootstrap/database locks remain separate.
Every resume verifies the persisted bundle path is exactly the canonical
`$root/bundle` and its installed file inventory/bytes match the selected signed
manifest. This is repeated immediately before every shared Compose command,
including manual initialization, activation, status and token rotation.
Extra, modified or missing installed files cannot execute or be silently
overwritten. Only `install`/`prepare` while the durable phase is `initializing`
may fill missing files from an interrupted initial extraction; existing
partial files must still match the signed release exactly.

## Resume, token rotation and readiness

Rerun the same `install` to resume incomplete initialization; manual users
repeat the interrupted step. Established credentials/keys are never regenerated.
Unknown outcomes remain unknown until reconciled; never delete bootstrap rows,
metadata, volumes or key material to retry.

If output was lost or the token expired, use the same template with
`rotate-token --token-output-name setup-token-2`. Choose a **new basename**:
rotation calls only the shared producer with the same IDs and cannot overwrite
an output or reset a completed operator. Keep protected token files out of
screenshots and logs. A ready installation is not reinstalled; use a future
explicit qualified upgrade procedure instead.

Exit `0` means the requested preflight/staging step succeeded, or final
`ready`; exit `2` means resumable `awaiting_dns_tls`/`awaiting_operator`;
exit `1` means failure, not fresh state or success. JSON progress/status never
includes credentials. Readiness requires the host bootstrap state, trusted
exact-origin HTTPS API health/release identity, no-store HTTPS setup transport,
matching dashboard runtime config and its HTTPS setup route. Only a shared
`ready` bootstrap state plus those checks reports `ready`. Container health
alone is insufficient; every required service must also be running, and each
configured service healthcheck must pass. Compose startup never pulls missing
images implicitly after verification. DNS/ACME failures retain state: correct DNS, inbound
ports and certificate issuance, then run `status`. A ready result does not
qualify a release, authorize deployment or prove backup/restore.

The root sibling `<root>.installation.lock` is persistent and never removed.
`installation.json` schema 1 stores exact release/schema/digest identities,
original IDs, paths/configuration, phase and last successful operation;
it contains no credentials. Preserve both for T44's maintenance handoff.
