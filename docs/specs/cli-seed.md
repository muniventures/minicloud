# CLI seed: provision the first Minicloud control plane

Status: source implementation submitted; not published or qualified. See the
[implemented interface](../seed.md) and [delivery contract](../cli-distribution.md).
Scope decisions confirmed
with the user on 2026-11-12. This is new work after the public pre-v1 code-only
milestone, not evidence that the earlier milestone included host provisioning.
Tracking issue: [#80](https://github.com/muniventures/minicloud/issues/80).

**Current contract:** [#90](https://github.com/muniventures/minicloud/issues/90)
supersedes the machine-bound journal and hidden automation identity in this
historical design. The [stateless seed interface](../stateless-seed.md) specifies
the implemented protected configuration, caller-held identity, observational
inspection, classifications, uncertainty and recovery boundaries. Existing
legacy keys/journals remain user-owned and compatible; they are not required
for the stateless interface and are never automatically imported or removed.

## Outcome

A user downloads a prebuilt CLI, or builds the CLI from canonical source, then
runs `minicloud seed` without an existing Minicloud instance or login. The CLI
uses the user's Hetzner, DigitalOcean or Vultr account to create a dedicated VPS,
install the exact signed Minicloud server release from container images, and
hand off to the existing first-operator browser setup.

The user no longer needs to create the control-plane VPS or manually pull images.
The control plane remains distinct from the application VPSs it later manages.
This is not a new account-seeding algorithm, hosted service or single VM image.

## Confirmed decisions

| Question | Decision |
| --- | --- |
| Providers in the initial feature | Hetzner, DigitalOcean and Vultr, with the same lifecycle guarantees. |
| Region and size | User must explicitly select both; no silent defaults. Show cost and require confirmation before billable creation. |
| DNS | User creates DNS records; show exact records and wait/resume for propagation. No DNS-provider credentials or automatic DNS changes. |
| Initial access | A domain and valid public HTTPS are required. IP-only access is deferred, not implemented by relaxing HTTPS or CORS. |
| Provider credential after bootstrap | Offer explicit opt-in to register a provider account for app provisioning after authenticated operator onboarding. Default is no import. |
| CLI delivery | Restore Linux x64/arm64, macOS arm64 and Windows x64 binaries; retain source installation. No Intel macOS target in this scope. |

This feature supersedes the prohibition on future CLI binary publication in
[T20](pre-v1-release/subspecs/20-cli-distribution.md) and its source-only T00
distribution decision **when the new delivery implementation merges**.
Historical milestones/releases remain accurate and immutable. Source acquisition,
canonical instance/auth APIs and separate CLI/server release channels remain.
The current source-built customer workflow need not switch to binaries.

## Proposed command interface

The following interface is implemented in source; release/provider qualification
and actual signed server publication remain separate gates:

```sh
minicloud seed --provider hetzner \
  --region <provider-region-id> --size <provider-size-id> \
  --domain cloud.example.com --app-domain apps.example.com \
  --server-version server-v<VERSION>

minicloud seed status <operation-id>
minicloud seed resume <operation-id>
minicloud seed cleanup <operation-id>
```

`--provider` accepts `hetzner`, `digitalocean` or `vultr`. Interactive mode may
prompt for omitted non-secret fields and list read-only region/size catalogs,
but region and size must be explicitly selected. Noninteractive invocation
must supply all required choices and an explicit billable-action approval
bound to the reviewed plan; it must never wait for hidden interactive input.
An ordinary retry/confirmation must not approve changed price, size, region,
resource count, server release or account. Status/help perform no mutations.

Prompt for provider API credentials without echo. Support a protected credential
file for automation; reject plaintext `--api-key <value>` arguments. Do not
silently reuse `MINICLOUD_TOKEN`, a selected instance, or application credentials.
Document required provider scopes and account restrictions per provider.
Authentication/quota/catalog/region/size failures occur before VPS creation.

The domain denotes the dashboard hostname, not a DNS zone to purchase.
`cloud.example.com` maps to:

- dashboard: `https://cloud.example.com`;
- API: `https://api.cloud.example.com`;
- application suffix: separate explicit `--app-domain`, such as
  `apps.example.com`, following the existing installer configuration contract.

Validate hostnames, reserved-domain overlaps, supported Ubuntu 24.04 image and
CPU architecture, size capacity, account, release and local prerequisites before
creation wherever possible. Do not guess provider image IDs or SSH login users.
The example selects an exact server tag; there is no repository-wide `latest`
selection, invented release, source-build fallback or implicit publication.

## Lifecycle and failure behavior

1. **Plan.** Resolve the selected account, region, size, architecture, base image
   and exact server release. Verify release authenticity/compatibility before
   creating resources. Show all planned resources and their account/region,
   recurring price, currency, billing unit and retrieval time. Identify tax,
   public IPv4, bandwidth and other exclusions. If authoritative cost data
   cannot be obtained, block creation with an actionable explanation rather than
   presenting a fabricated estimate. Display that resources remain billable
   while waiting for DNS or after a local interruption.
2. **Confirm.** Obtain explicit approval of that plan before creating any
   billable resource. Nothing purchases domains, credits or subscriptions.
3. **Create.** Generate a dedicated seed automation SSH key, kept separate from
   operator keys. Create one tagged control-plane
   VPS with the selected Ubuntu image. Record the operation ID and every
   resource identifier as soon as known. Do not create an app VPS, shared network
   or load balancer implicitly.
   Planning also offers additional operator SSH access: accept public-key files
   or generate a separate local ed25519 pair with `ssh-keygen` when available,
   asking about passphrase protection (default yes). Only public keys join the
   approved plan and are installed over pinned SSH after host preparation.
   Keep the automated seed identity separate; never upload operator private
   keys/passphrases or require an operator passphrase for automated resume.
4. **Establish host trust.** Obtain/verify the SSH host key through a documented
   provider-authenticated mechanism or explicit independent provider-console
   fingerprint confirmation, then pin it before transmitting secrets. Blind
   TOFU, `StrictHostKeyChecking=no` and `ssh-keyscan` alone are not verification.
   Missing verifiable host identity pauses the operation; mismatch fails closed.
   Establish least-required SSH access and document ingress prerequisites.
5. **Prepare the host.** Install pinned/documented installer prerequisites from
   authenticated official sources, using the supported host bootstrap path.
   Do not pipe unchecked remote code into a privileged shell. The existing server
   installer does not install Docker itself: this new seed orchestration must
   supply Docker/Compose/Buildx, Python, Cosign, OpenSSL and OpenSSH prerequisites
   before invoking it. No .NET/Node SDK or source compilation on the VPS.
6. **Await DNS.** Display dashboard/API A records for the assigned public IPv4,
   and AAAA only for deliberately supported/configured IPv6. Check actual
   resolution against the created host; reject conflicting/stale addresses.
   App wildcard DNS is a separate app-host routing concern and must not be
   pointed to the control-plane VPS by default. Allow safe pause/resume.
7. **Install.** Transfer protected configuration and invoke the existing exact
   signed server installer, retaining its manifest/provenance checks, secret
   handling, Compose ordering, persistent installation ID, migrations, OpenBao
   initialization, recovery safeguards and resumable readiness. The installer
   pulls digest-pinned images; seed must not duplicate its implementation or
   directly improvise `docker run`, bootstrap state or SQL.
8. **Verify and hand off.** Require valid TLS and readiness at the configured
   dashboard/API origins; validate `/api/instance` product/protocol/compatibility.
   Show/open the dashboard `/setup` URL without secrets in its path/query/
   fragment. Save a named CLI instance only after validation, without silently
   replacing another instance or its authentication.
9. **Optional app-provider registration.** After browser setup and ordinary CLI
   authentication/authorization, offer the explicit opt-in described below.
   Completing infrastructure seed does not mean login or app deployment passed.

Durable phases should distinguish `planned`, `creating`, `awaiting_ssh_trust`,
`preparing_host`, `awaiting_dns`, `installing`, `awaiting_browser_setup`,
`complete`, `reconciliation_required` and `failed`; do not conflate VPS creation
with a healthy server or completed browser onboarding.

Every mutating phase is resumable using a per-operation lock and durable
non-secret journal. Persist provider resource IDs, selected release/source,
addresses, domain/config identity and installer progress. Credentials are
re-supplied on resume unless the user deliberately chooses a documented secure
credential store; no plaintext key in the journal. Keep secret files separately
protected with Unix permissions or equivalent Windows ACLs.

Provider create timeouts must reconcile using recorded IDs/unique operation
tags and supported idempotency before any retry. A provider with uncertain
creation and no conclusive reconciliation requires operator action, not another
create request. Never adopt an unrelated resource by name or mutate an existing
host lacking matching operation/installation identity.

Cancellation/interruption never silently deletes a possibly installed server.
Print retained resources and billing implications. `cleanup` presents exact
operation-owned resource IDs and requires separate destructive confirmation;
shared/user-supplied keys, existing instances and unrelated provider resources
are excluded. Refuse uncertain ownership. Completed installations require the
normal maintenance/decommission boundary, not a convenient seed rollback.

## Credentials, recovery and browser boundaries

- Provider credentials stay on the workstation for seed API requests. Never
  place them in cloud-init/user-data, process arguments, URLs, tags, logs,
  release assets, journal, browser storage or the VPS's bootstrap configuration.
  Redact provider error bodies and disable credential-bearing request tracing.
- The setup token is not an API access token. Retrieve it only over pinned SSH
  into protected local output on explicit request; print the protected path,
  not the token. Never auto-submit it through a command-line browser URL.
  Reuse host token-rotation/reconciliation behavior; no bypass of origin checks.
- Existing independent OpenBao recovery material must be securely exported to
  protected operator custody off the VPS, with explicit acknowledgement before
  seed reports full completion. A recovery directory on that same VPS is not
  an independent copy. Document permissions, Windows handling and safe resume;
  never upload recovery keys to the provider or silently discard them.
- Public registration remains disabled. Browser setup creates the operator/
  workspace using the existing shared bootstrap producer, then normal login.
  Do not mint machine tokens or grant provider permissions from a setup token.

### Optional provider-account registration

An unselected opt-in is the default. Explain that registering the credential
gives the new Minicloud control plane authority to provision billable app
resources; let the user provide a different, appropriately scoped key instead.

After operator setup, use ordinary CLI browser-consent login and existing
authorized provider-account APIs in the explicitly selected workspace.
Verify that token scopes and the actor's workspace role permit account creation;
otherwise direct the operator to the existing dashboard flow. Do not invent a
bootstrap-only anonymous credential-import endpoint or broaden token scopes.
Send the key only to the verified HTTPS API after explicit import consent.

Name/ID reconciliation must prevent duplicate provider-account creation on
resume; conflicting accounts require confirmation, not credential replacement.
Registering an account does not automatically adopt the control-plane VPS into
the application inventory, create a plan, or deploy an app. Import failure leaves
the healthy control plane intact and is reported as an optional pending step.

## DNS, CORS and future domain changes

Seed writes the existing configuration's exact `consoleUrl`, `apiUrl`,
`consoleHostname`, `apiHostname` and separate `appDomain`. API CORS, cookies,
CSRF/origin validation, Kratos return URLs, Caddy and dashboard runtime config
must agree on these HTTPS origins. Never add wildcard origins, allow arbitrary
provider IPs or disable browser protections to make bootstrap work.

IP-only HTTP/HTTPS, single-origin IP routing and certificate exceptions are
explicitly deferred by the user after discussing the auth/TLS implications.
Record this future UX request; do not describe it as supported.

Additional app domains continue through existing authorized app-domain APIs/
screens. Moving the dashboard/API domains is a different operation: the current
installer enforces original configuration matching, so seed must not advertise
editable established control-plane domains. A future coordinated domain-change
feature needs DNS/TLS, CORS/CSRF, cookies, identity callbacks, saved CLI instances
and recovery/config consistency; direct metadata edits are not a migration.

## Restore binary CLI delivery

Add a dedicated canonical CLI publication workflow for `vMAJOR.MINOR.PATCH`
with optional prerelease, independent from `server-v*` tags. Build the existing
CLI as self-contained, untrimmed output for:

| Platform | RID |
| --- | --- |
| Linux x64 | `linux-x64` |
| Linux arm64 | `linux-arm64` |
| macOS Apple Silicon | `osx-arm64` |
| Windows x64 | `win-x64` |

Preserve `minicloud`/`minicloud.exe`, public installer paths, version/source
metadata and existing configuration. Use versioned archives, a signed checksum
inventory and documented canonical workflow/source provenance. Verify
authenticity and archive path/type safety before extraction/execution; ordinary
adjacent checksums are not publisher authentication. Check version/help before
atomic replacement, retaining a working installation on failure. Never select
CLI assets from a server release or mutate historical release assets.

Offer explicit binary/source installation modes through the existing installers;
binary becomes the normal supported-platform path after qualified publication.
Keep exact canonical SHA/tag source builds with Git/.NET 10 available and
documented. Do not silently downgrade, switch channel, build source after failed
signature verification, or install an SDK to hide unavailable binaries.
Pin dependency/actions/tool versions and document native OS/runtime constraints.
Source-built customer CI keeps its current inputs and isolated checkout.

## Implementation seams and delivery slices

Use the existing CLI dispatcher in `modules/cli/Commands/CliApplication.cs`:
seed must bypass `EnsureInstanceAsync`, like offline help/instance discovery.
Keep CLI/platform-provider transport independent from worker/EF/OpenBao runtime
initialization. Inspect `modules/common/Providers/IVpsProviderClient.cs`, SSH-key
contracts and the Hetzner/DigitalOcean/Vultr clients for reuse, but do not add a
dependency on the entire running server to provision the server itself.
Prefer narrowly reusable adapters; preserve existing app provisioning behavior.

Suggested implementation slices, with exclusive ownership and actual merges
before dependent integration:

1. Restore verified binary delivery/installers and preserve source mode.
2. Freeze seed plan/journal/credential/host-trust interfaces and implement CLI
   offline command dispatch plus confirmation/reconciliation orchestration.
3. Implement all three provider adapters against those contracts, in parallel
   where their files do not overlap; no initial release with only one supported.
4. Integrate verified host preparation, existing server installer, manual DNS,
   recovery export and browser/instance handoff.
5. Add authenticated opt-in provider registration and user documentation.
6. Perform separately authorized end-to-end/clean-machine qualification, then
   publish approved CLI/server artifacts through their distinct pipelines.

Binary delivery can proceed independently of seed, but a binary-only delivery
PR does not complete this feature. Exact server release assets must actually
exist and be verified before an end user can complete seed. The current
`0.0.1-rc.1` metadata is unpublished/unqualified, not that dependency fulfilled.
Private T70 integration is not a prerequisite.

## Acceptance criteria and validation plan

- A clean binary install on each of the four targets reports correct version/
  source/help without an SDK. Exact source installation still works. Wrong
  signatures/digests, unsafe archives, unavailable releases or unsupported
  platforms fail without replacing an installed CLI.
- With no configured instance/login, seed plans independently for each provider;
  invalid credentials, scopes, quotas, sizes, images or domains produce
  actionable errors without creating resources.
- The selected account/region/size and current cost are shown and confirmed
  before mutation. No DNS, app VPS, provider import or other billable resources
  appear without their specified approval.
- Focused mocked tests cover secret redaction, confirmation refusal, ambiguous
  create timeouts, interrupted journal writes, resume without duplicate hosts,
  concurrent invocation, SSH identity mismatch, DNS wait, TLS/readiness errors,
  install resume, cleanup ownership and protected recovery export.
- Origin/config integration checks cover exact HTTPS dashboard/API origins,
  CSRF/cookies/identity redirects and reject IP-only/insecure fallback.
- Setup uses the existing first-operator flow, with no secret URL or auth bypass.
  Instance persistence does not overwrite unrelated selections/credentials.
- Provider import requires explicit consent plus normal auth/permissions,
  handles resume/conflict, and optional failure does not invalidate installation.
- In separately authorized disposable accounts, qualify creation/resume/install/
  recovery handoff and owned cleanup on each provider and supported server
  architecture combination. Record actual costs, resource IDs, artifact
  versions and cleanup evidence without secrets.

This specification creates no resources, publishes no binaries/images and
authorizes no credential use. Tests/builds are a future implementation/
qualification plan; none were run to author this document. The earlier
no-tests code-only session is not an acceptance pass for this new feature.
