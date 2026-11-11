# T40–T44 — Packaged self-hosting

Repository: `muniventures/minicloud`. Status: T40 merged (#45); T41 in review on `feat/t41-self-hosting-stack`; T42–T44 not started.
Authority: [packaged self-hosting specification](../../v1.0-self-hosted-installation.md). Its original T00–T90 IDs are local to that document; the IDs below are the umbrella assignments. Implement the stated scope without adding installation modes or rewriting deployment behavior.

| Task | Deliverable | Hard dependencies | Parallel work |
| --- | --- | --- | --- |
| T40 | Public multi-architecture images and versioned release bundle | T00, T20 distribution contract | T10, T30, T41 |
| T41 | Persistent dependency stack and runtime dashboard configuration | T00, T30 schema/bootstrap contract | T40; T30 implementation and T31 with separate files |
| T42 | First-operator browser setup and provider onboarding | T30, T31, T41 merged | T43 preparation; SSH/number work outside setup/auth files |
| T43 | Server installer and manual Compose parity | T40, T41, T42 merged | T51/T52/T61/T62 outside installer files |
| T44 | Upgrade, coordinated backup/restore and recovery | T43 merged | Remaining independent customer-surface tasks |

Each row is a separate Copilot issue/PR. Agents run focused checks with synthetic fixtures. Codex owns final combined and disposable-host validation after implementation merges. Preparing a release workflow does not authorize publishing images/releases or using protected-environment credentials.

## Frozen T00 packaging interfaces

[T00](00-contracts.md#frozen-integration-decisions) fixes the source-only CLI pin/version policy, server workflow/tag/channel/asset names, manifest compatibility fields and Sigstore trust identity. [T30](30-identity-and-sessions.md#frozen-t00-wire-and-seed-contract) fixes runtime public configuration, account/setup DTOs and shared host bootstrap operations. Implement these interfaces, not alternative discovery, auth or seed paths. In particular, T42 owns setup HTTP transport only; T30 owns the shared service, host entry point and schema. T41 consumes its protected-input contract.

T41 owns all three existing Dockerfiles and Compose until its handoff. T40 may author image workflow definitions against their frozen build/startup interface, but must request and record T41's handoff before modifying those files. Contract-only dependencies permit scaffolding, not merging references to nonexistent runtime services. T43/T44 then receive shared Compose/maintenance files sequentially.

The shared image interface is repository-root build context with `modules/api/Dockerfile`, `modules/worker/Dockerfile` and `modules/dashboard/Dockerfile`; API/dashboard serve container port 8080, worker has no public port. API/worker retain `dotnet Minicloud.Api.dll` / `dotnet Minicloud.Worker.dll` entry points; the API additionally dispatches T30's host bootstrap operations. Dashboard accepts validated public configuration at startup, not installation-specific build arguments. T41 removes Firebase/Vite hostname build inputs; T40 supplies source/version OCI metadata without secrets. The current worker Dockerfile hardcodes its embedded runtime agent to `linux-x64`: T41 must expose explicit agent-RID build input/plumbing and T40 must supply/verify it rather than relabeling an x64 payload as arm64. Application VPS agent architecture must follow supported target placement; a native arm64 control-plane build alone is not evidence that cross-architecture managed VPSs work.

## T40 — Images and release assets

Own new server-specific public release workflow/assets, image build wiring and server manifest/compatibility schema. Existing CLI workflow/installers belong to T20. Existing Dockerfiles may be touched only with an explicit handoff from T41; freeze build arguments and startup requirements first.

- Build API/dashboard/worker for `linux/amd64` and `linux/arm64`. Produce a versioned manifest and Compose/configuration/installer/maintenance bundle using the existing repository and GHCR patterns.
- Resolve and pin dependency/image digests, record exact source revision and compatibility, verify signatures/provenance with a documented trust root, and refuse tampering or mismatched assets. Avoid floating dependency tags in installed state.
- Use a distinct `server-v*` release trigger and server channel. Keep server discovery independent of repository-wide `releases/latest` and old CLI executable archives. Consume T20's source-CLI contract; do not restore binary CLI publication.
- Implement release verification and anonymous-pull checks, but leave actual publication/permission configuration to the authorized release stage. A local image build is not evidence that GHCR anonymous pulls are enabled.

Acceptance: focused workflow/manifest tests reject mixed releases, unsupported architectures and mismatched source/digests; build checks cover both architecture definitions. Record any unavailable architecture/provenance/pull check as a gap.

T40 implementation/handoff: [release tooling and exact schema/trust contract](../../../../packaging/server/README.md).
The new workflow is deliberately gated on T41's Dockerfile/runtime handoff,
T41/T43/T44's complete bundle and the integration owner's tested release input.
Existing Dockerfiles/Compose remain untouched. The archive carries the runtime
manifest core; a detached manifest binds its final archive checksum, avoiding a
circular hash. No release publication, real image builds, signing, anonymous
pulls or installation/hosted qualification are claimed by local synthetic checks.

## T41 — Shared stack and universal dashboard

Own `compose.self-host.yml`, server configuration/schema/templates, dependency initialization/migration scripts, and dashboard `app/core/config/runtimeConfig.ts` plus startup/image plumbing. T31 owns `AuthProvider` and ordinary session flows; consume its interface without editing it concurrently. This owner hands off Compose/shared maintenance helpers before T43/T44 modify them.

- Bundle private PostgreSQL, persistent OpenBao, Kratos and Caddy using the existing self-hosting requirements. Expose only intended HTTPS browser/API routes, never database/secrets/identity administration ports.
- The same dashboard image accepts validated public API/auth origins and capabilities at startup. Never embed installation secrets or require rebuilding for a different hostname. Preserve exact-origin cookie/CSRF contracts from T30/T31.
- Use persistent, protected paths and independent credentials; scoped renewable service credentials replace bootstrap/root secrets during normal operation. Document restart/unseal/recovery dependencies without storing the only recovery material inside encrypted OpenBao.
- Serialize one-shot migrations and wait for dependency/application readiness before enabling normal workers or management writes. Initial-volume scripts must not be the only upgrade path. Invoke T30's shared seed primitives; do not create another seed implementation.
- Package startup/maintenance primitives without provisioning customer VPSs or performing real installation. Reject unknown populated state; preserve existing data/configuration on recognized restarts.

Acceptance: synthetic startup/configuration/exposure checks, identical dashboard image under two configurations, initialization replay/interruption and existing-volume migration checks. Secrets/admin endpoints never enter public configuration.

## T42 — First operator and onboarding

Own setup controllers/DTO transport, dedicated setup screens/routes and provider/plan onboarding guidance. Reuse T30's service/command and T31's completed auth state. Coordinate route-file ownership; do not rewrite `AuthProvider` or duplicate identity admission logic.

- A fresh installation admits exactly one operator/workspace through an installation-bound, expiring one-time token over HTTPS. Tokens use protected input/output, no query strings or routine logs.
- Setup completion is durable and serialized. Interrupted identity creation reconciles the exact operation; completed or otherwise populated installations cannot be claimed or have membership/password reset by installer replay.
- Use T30's host-only token rotation/password recovery. Password-only installation requires no Firebase, Google, SMTP, GitHub account or hosted identity account.
- Show distinct setup-required, temporarily unavailable and ready states. After setup, guide provider/plan creation through existing APIs; guidance must not allocate a VPS automatically or introduce another account/plan model.

Acceptance: concurrent setup, expired/reused token, interrupted seed, populated state, session/CSRF/origin rejection and normal ready-operator navigation have focused tests.

## T43 — Installer/manual parity

Own the distinct server installer, installation preflight/resume and manual-install documentation. Consume T40/T41 assets and T42 onboarding rather than copying initialization logic. No change to the old CLI installer URL's meaning; T20 supplies its explicit source-only transition.

- Install on dedicated Ubuntu 24.04 `amd64`/`arm64` from verified versioned assets, without source checkout, GitHub credentials, local compilation or a build SDK on the server.
- Check configuration, OS/architecture, ports, DNS/TLS, capacity, permissions and existing state before mutation. Do not overwrite an existing Docker engine, firewall/SSH policy, unknown installation or populated volume.
- Installer reruns resume their own operation without resetting credentials or recreating volumes. Recognized installations use upgrades. DNS/ACME failure preserves state and reports a resumable action; there is no insecure public-admin fallback.
- Manual Compose consumes the same bundle, migrations, seed, readiness and configuration. A container-running state is not ready; report awaiting DNS/TLS, awaiting operator, ready or failed accurately.
- Encode minimum checks with documented values; measured supported hardware claims wait for T90/T91 measurements. Do not invent a release-supported minimum from an untested guess.

Acceptance: focused preflight, conflict, failed download/trust, incomplete initialization and replay tests; installer/manual call the same primitives. Real host qualification remains Codex-owned.

## T44 — Upgrade and full recovery

Own existing shared maintenance primitives after T41/T43 handoff, version-aware upgrade/backup/restore commands, scheduling/retention/status and concise recovery documentation.

- Installation-wide locking, capacity/trust/compatibility checks, write quiescence and successful coordinated pre-upgrade backup precede migrations. Drain active work; do not kill accepted provisioning merely to progress.
- Back up application/identity databases, OpenBao state and separately recoverable keys, artifacts, installation configuration/credentials and TLS state or a tested reissuance procedure. Encrypt bundles, support scheduled/on-demand capture, retention and an explicit off-host copy procedure.
- Reject incomplete/tampered/incompatible restores before mutation. Rehearsal/restored workers cannot accidentally manage live resources. Explicit production transfer fences the old worker before admitting new writes.
- Preserve app/server IDs, keys, memberships and data. Keep customer apps running independently. Allow image rollback only with proven schema/data compatibility; otherwise use coordinated recovery while writes remain fenced.
- Stopping/removing containers retains persistent data and customer VPSs. Data erasure is a separate exact-target confirmed action; no broad prune or provider cleanup.

Acceptance: focused locking, incomplete backup, failed migration/activation, restore integrity, compatibility and retained-data tests; no database-only restore is presented as full recovery. Preserve uncertainty about any unqualified live recovery behavior.

## Assignment prompt

Implement only the assigned row T40/T41/T42/T43/T44 after its hard prerequisites merge. Read the linked public installation spec and [umbrella](../1.pre-v1-release.md), stay within the declared file handoff, add/run focused checks and submit one PR. Report missing capabilities; do not publish, deploy, use real identities or claim Codex's final acceptance.
