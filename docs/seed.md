# Provision the first control plane

`minicloud seed` has a source implementation, **not live-provider or
clean-machine qualification**. No new CLI/server release has been published
by this feature. The current `server-v0.0.1-rc.1` candidate is unpublished,
unqualified and does not declare this new CLI source compatible. It cannot
complete seed. Missing assets, signatures or compatibility fail before
provider creation; do not publish or substitute artifacts to work around that.

Seed works without a configured instance, `MINICLOUD_TOKEN`, or login.
It creates a **dedicated** Ubuntu 24.04 control-plane VPS, not an app VPS.
It uses the [existing signed installer](server-installer.md) and shared
[operator bootstrap](bootstrap.md), never a second Compose/SQL/bootstrap engine.
[CLI distribution](cli-distribution.md) covers exact binary and source selection.

## Before starting

- Own a public dashboard hostname and a separate app suffix. For a dashboard
  `cloud.example.com`, the API is exactly `api.cloud.example.com`. Replace
  these examples with domains you control. No domain is purchased.
- Have a funded provider account with appropriate permissions, remaining quota
  and capacity. Region and size are explicit, not automatically selected.
- Install OpenSSH client tools (`ssh`, `ssh-keygen`, `ssh-keyscan`) and
  independently verified **Cosign 2.6.5** on the workstation. Linux/macOS also
  require their standard `stat`, `id` and `sync` utilities. Windows needs its
  OpenSSH optional client feature. No local Docker, Node or SDK is needed
  by a binary CLI. Use a local filesystem, not network/UNC storage for journals.
- Allow root key-based SSH on TCP 22 from this workstation; provider browser
  console access is required for independent host identity confirmation.
  Allow public TCP 80/443, optional UDP 443 for HTTP/3, and outbound HTTPS for
  Ubuntu archives, GitHub, GHCR/dependency registries, Sigstore and ACME.
  Seed does not create firewalls or change ingress/DNS policies.
- Keep `$XDG_CONFIG_HOME/minicloud/seed/` (normally
  `~/.config/minicloud/seed/`) private and persistent. Preserve the journal,
  generated private key, pinned host key and persistent lock. Never copy an
  operation directory to another host to claim ownership there.

Provider permissions and catalog sources:

| Provider | Required account/key access | Authoritative price/catalog |
| --- | --- | --- |
| Hetzner | Project **read/write** Cloud API token, servers, SSH keys, images, locations, server types and pricing. A read-only token cannot create. | [`/v1/pricing`](https://docs.hetzner.cloud/reference/cloud#tag/pricing), server types/locations and system images. Prices include a separate IPv4 line; net hourly and monthly cap, API currency, tax/traffic exclusions shown. |
| DigitalOcean | Account/VPC read; Droplet read/create/delete; SSH-key read/create/delete; region/size/image read (and required dependent read scopes). Active, email-verified account and an **existing default VPC** in the selected region. No project assignment or implicit shared-network creation is performed. | [`/v2/sizes`](https://docs.digitalocean.com/reference/api/reference/): USD hourly/monthly cap, included transfer. Public IPv4 included; tax/overage/optional services excluded. Droplet count is checked against account limit. |
| Vultr | API key with `provision` ACL, account/inventory/catalog access and an IP access-list entry for this workstation. Funded active account. | [`/v2/plans`](https://www.vultr.com/api/): USD monthly rate and bandwidth allowance; provider hourly/monthly-cap terms displayed, **no invented hourly rate**. Regional availability and official OS list checked. |

Hetzner and Vultr do not expose a conclusive remaining-instance-quota check
through these read APIs; this restriction is in the reviewed plan. Read-only
checks cannot prove write scopes or reserve capacity on any provider. A later
provider rejection is an error, never a successful allocation or reason to
retry an uncertain create. Consult the provider console for quota/write access
before approving. No subscription, credits or extra resources are purchased.
Hetzner has no profile endpoint for the project-scoped token: the plan binds
that scope using an operation-specific credential fingerprint. Resume requires
the same credential, not an unverified claim about another token's project.

## Plan, approve and create

For automation, create a credential file using your secure editor/secret
manager, in an owner-only directory; Unix directory `0700`, file `0600`/`0400`.
No links, traversal or group/world-writable ancestors are allowed.
On Windows, disable inherited ACL grants and allow only the current user
(SYSTEM/Administrators are accepted trusted principals). Do not use shared
or temporary folders. Supply only the credential, with an optional final newline.
Without a file, an interactive terminal gets a hidden prompt; redirected input
never waits for that prompt. Environment provider keys and `MINICLOUD_TOKEN`
are not silently reused. Plaintext credential arguments are rejected.

```sh
minicloud seed --provider hetzner \
  --region <location-id> --size <server-type-id> \
  --domain <dashboard-hostname> --app-domain <separate-app-suffix> \
  --server-version server-v<exact-published-compatible-version> \
  --credential-file <protected-local-file>
```

Use `digitalocean` or `vultr` with their exact region/size IDs. Read catalogs
in the provider console/API before invoking; seed never silently chooses.
The plan reports account/scope, image ID, architecture, root SSH user,
capacity, all recurring resource prices/currency/billing/exclusions, source
and retrieval time, and exact signed server release. Unsupported architectures,
unknown prices, unavailable Ubuntu images and inadequate capacity are refused.
The conservative provider floor is 2 CPUs, 2 GiB RAM, 20 GiB disk; the installer
also checks free disk. Neither floor is measured/qualified stack sizing.

No provider mutation occurs during planning. Copy the operation ID and digest:

```sh
minicloud seed resume <operation> --approve <reviewed-plan-digest> \
  --credential-file <protected-local-file>
minicloud seed status <operation>
```

Approval authorizes one generated dedicated SSH key and one tagged/labeled VPS,
plus Hetzner's auto-owned public IPv4. No existing SSH keys are modified or
deleted. Pricing/account/image/release/placement/resource changes invalidate
the old approval before creation. Provider keys stay local, absent from
process arguments, cloud-init, journals, tags, browser state and host config.
Provider errors and subprocess diagnostics are suppressed rather than logged.

### Operator SSH keys

Interactive planning offers to add existing public keys, generate an operator
key, or skip. These keys are **additional operator access**, separate from
seed's unencrypted, protected automation identity. They are included in the
reviewed plan and approval digest; they cannot be changed by resuming an
already approved operation.

Use `--ssh-public-key <public-file>` (repeatable, at most 16 keys) to supply
OpenSSH ed25519 or RSA keys of at least 3072 bits. Do not supply private keys,
`authorized_keys` options or multiple keys in one file. Key comments are not
uploaded. Only the canonical public key is stored in the plan.

Use `--generate-ssh-key <new-private-file>` to generate an ed25519 pair with
local `ssh-keygen`. Its parent directory must already be owner-only (`0700`
or a protected Windows ACL); existing files are never replaced. The CLI asks
whether to protect the key with a passphrase, default **yes**. `ssh-keygen`
itself reads and confirms the hidden passphrase, so it never enters Minicloud's
arguments, journal, logs or VPS. Empty passphrases are rejected when protection
was selected; partial/generated files are retained for operator reconciliation.

Explicit `--ssh-key-passphrase` selects protected generation and still requires
an interactive terminal. `--no-ssh-key-passphrase` explicitly opts out and
supports unattended generation; no prompt is silently answered. Neither option
accepts a secret value. The private key remains at the selected local path
and is never uploaded, imported to a provider, used by seed automation or
deleted during cleanup.

```sh
# Add an existing public key (repeat the option for additional keys):
minicloud seed <other-required-options> --ssh-public-key /home/operator/.ssh/id_ed25519.pub
# Or generate a new operator key and answer the passphrase question:
minicloud seed <other-required-options> --generate-ssh-key /home/operator/.ssh/minicloud_operator
```

Noninteractive planning with no SSH-key options preserves the previous behavior:
no additional operator keys. `--no-operator-ssh-key` explicitly skips the prompt
in a terminal. After independently pinning host identity and preparing Python,
seed installs the approved public keys into root's `authorized_keys` over
pinned SSH, preserving the bootstrap key and existing entries. This update is
idempotent on resume and does not create/delete extra provider key objects.
If installation fails, seed retains bootstrap access and reports failure;
do not assume operator access exists until that step succeeds. Connect using
your retained private key and the VPS address after host identity verification.

`status` is local, read-only and does not prove live health. `resume` revalidates
the signed release and account, reconciles unique operation tags/recorded IDs,
then continues. Creation-only quota, catalog, availability and pricing checks
apply before the first create intent. Once creation may have begun, resume
preserves the approved plan and reconciles the VPS without requiring another
free quota slot or a still-orderable size/image. Original credential/account
binding and recorded resource ownership still apply.
An uncertain create with no conclusive match **never creates
again**: it remains `reconciliation_required` for provider-console/support
reconciliation. Names alone are not server ownership. Pending journal writes
block resume; preserve both `operation.json` and `operation.pending`, never
remove locks or edit IDs to retry. A failed initial release/plan verification
has not made a resumable provisioning plan or created provider resources.
`resume` exits `2` while a handoff/wait/reconciliation is pending and `0` only
at `complete`; inspect the journal phase rather than equating a created VPS
with a ready installation. Read-only plan/status and a successful export can
return `0` without claiming server completion.

## Host identity, preparation and DNS

The CLI pauses before SSH preparation. Open the **provider's authenticated
browser console**, not an SSH connection based on an unverified scan, and run:

```sh
ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub -E sha256
```

Independently verify the console belongs to the exact recorded server ID.
Pass that fingerprint as an explicit assertion:

```sh
minicloud seed resume <operation> --credential-file <protected-local-file> \
  --confirm-console-fingerprint SHA256:<console-fingerprint>
```

`ssh-keyscan` supplies only a candidate, compared against that independent
fingerprint. The verified key is pinned; subsequent connections use strict
checking, only that dedicated identity and no SSH config, password/interactive
fallback, agent forwarding or global trust-file bypass. Mismatch fails closed.
Lost keys/rebuilt hosts require reconciliation, not replacing the pin.

On the owned dedicated Ubuntu 24.04 host, the embedded preparation script uses
Ubuntu's signed official `noble`/updates/security archives for Docker Engine,
Compose v2, Buildx, Python 3, OpenSSL and OpenSSH, retaining distribution
security updates rather than pretending a measured package matrix exists.
Cosign **2.6.5** is downloaded from its exact official release and checked
against architecture-specific hashes pinned in source. No remote script is
piped into root; no .NET/Node SDK, source build or provider credential is sent.
Password SSH login is disabled, root access is key-only, and forwarding/X11
are disabled. Existing conflicting policy/state fails closed.

Host orchestration inputs/assets/recovery are under
`/var/lib/minicloud-seed/<operation>/`, with a durable operation binding at
`/opt/minicloud.seed-operation`; the install root remains `/opt/minicloud`.
The signed installer still owns its **own original installation/operation IDs**,
config validation, locks, Compose ordering, migrations and OpenBao initialization.

Seed prints **two exact A records**, dashboard and API, for the assigned IPv4.
Remove conflicting/stale A and AAAA records. IPv6 is disabled in this slice.
Resume after propagation; both workstation and installer check DNS against the
owned host. **Do not point app wildcard DNS at the control plane.** Additional
app-domain routing is configured later through ordinary authorized app-domain
APIs/screens for actual app VPSs. Installation uses exact public HTTPS origins;
there is no TLS bypass, HTTP fallback, wildcard CORS or anonymous registration.

## Recovery and first operator

After trusted HTTPS readiness, seed reports `https://<dashboard>/setup` without
any secret in the URL. Export recovery independently to the workstation:

```sh
minicloud seed export <operation> --recovery-dir <new-local-owner-only-directory> \
  --setup-token
```

This exports original OpenBao recovery shares/root token, root-token file,
private CA key/certificate and an installation metadata snapshot. They are
powerful secrets: protect/encrypt independent custody, verify recoverability,
and never upload them to a provider. The same-VPS recovery directory is **not**
an independent copy. Interrupted export resumes only at the originally
recorded directory, compares existing bytes and never overwrites conflicts.

Only explicit `--setup-token` exports the one-time token to a protected file;
the CLI prints the path, not its contents. Enter it manually in the browser's
normal `/setup` form. An expired/lost token can use the same command with
`--setup-token --rotate-setup-token`, invoking the **existing signed shared
producer** with a durable new output name. Uncertain rotation is reconciled
under that same name, never silently replaced or used to reset a ready operator.

```sh
minicloud seed export <operation> --ack-recovery <installation-id>
minicloud seed resume <operation> --credential-file <protected-local-file> \
  --instance-name <new-name>
```

`complete` requires trusted exact-origin HTTPS/API product/protocol/release
identity, the installer's ready browser-bootstrap state, successful independent
export and explicit acknowledgement. Saving the validated instance never changes
another active selection/token; select it using `minicloud instance use <name>`.
Infrastructure completion does not mean login or app deployment passed.

## Optional provider registration

Default is **no import**. After normal browser onboarding/recovery, explicitly
opt in; a different appropriately scoped credential can be supplied:

```sh
minicloud seed register-provider <operation> --register-provider \
  --organization <workspace-id> --provider-name <name> \
  --credential-file <protected-local-file>
```

This performs ordinary browser-consent login and checks the existing workspace
authorization policy through authenticated `GET /api/providers/permissions`.
Default browser-login scopes intentionally **do not** include provider
management, so use the existing dashboard flow, or explicitly select an
instance-scoped operator API key with `providers:read` and `providers:write`
and repeat with `--use-existing-auth`. Do not broaden default login scopes.
Unset `MINICLOUD_TOKEN`; the feature never picks an unrelated environment token.
Existing token directories/files must also satisfy the private-file contract.

Before any key is sent to the verified HTTPS API, the CLI displays the grant of
billable app-provisioning authority and requires
`--confirm-provider-import <exact-digest>` bound to origin/workspace/name/type/
credential. Resume reconciles recorded account ID/name; a conflicting or lost
response requires independent verification and
`--confirm-provider-account <exact-existing-id>`. It never replaces credentials
or retries uncertain creation. Optional errors keep the healthy server intact.
No control-plane adoption, app VPS, plan or app deployment follows import.

## Retention and cleanup boundaries

Cancellation and DNS/SSH/TLS waits retain resources and **continue billing**.
There is no automatic rollback.

```sh
minicloud seed cleanup <operation> --credential-file <protected-local-file>
# Review exact operation-owned server/IP/generated-key IDs, then repeat with:
# --confirm-cleanup <separate-cleanup-digest>
```

Cleanup refuses uncertain ownership/creation and any operation where installation
may have begun. It checks server tag/ID/region/size/image and generated key
name/ID/public bytes; Hetzner IPv4 must still belong to that server and be
auto-delete. No shared or user-supplied key/resource is deleted. Asynchronous
deletion remains pending until provider absence is observed; verify console
billing too. Local keys/recovery are retained.

After installation starts, use the normal maintenance/decommission boundary,
not seed rollback. IP-only access and coordinated dashboard/API domain migration
are **explicitly deferred**. Established domains/configuration are immutable;
editing installer metadata is not a migration.

## Local seed tests

The targeted tests use mocked providers; they do not create an actual VPS or
qualify live-provider behavior. From the checkout root, with the .NET 10 SDK:

```sh
dotnet test modules/tests/Minicloud.Cli.Tests/Minicloud.Cli.Tests.csproj \
  --configuration Release \
  --artifacts-path .local/artifacts/seed-tests \
  --results-directory .local/artifacts/seed-tests \
  --filter 'FullyQualifiedName~Seed' -m:1
```

All protected-fixture tests require an owner-only checkout/worktree with safe
ancestors. `SeedFiles` rejects group/world-writable ancestors, including those
above the checkout; making only the fixture leaf private with `chmod` is
insufficient. Do not weaken validation, skip tests, or change shared repository,
home or other shared-directory permissions to get a passing run.

Instead, choose an existing operator-private location whose ancestors are not
group/world-writable, then create an isolated worktree there. Replace the
example path below with that location; run from the source checkout:

```sh
umask 077
git worktree add --detach /operator-private/location/minicloud-seed-tests HEAD
cd /operator-private/location/minicloud-seed-tests
```

Run the test command from this private worktree. Keep build outputs, results and
fixtures in its own ignored `.local/artifacts/`, not the original shared checkout
or a shared temporary directory.
