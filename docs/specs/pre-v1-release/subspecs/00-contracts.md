# T00 — Freeze shared contracts and ownership

Repository: `muniventures/minicloud`. Owner: one contract/integration agent.
Depends on: none. Can run in parallel with: read-only investigation of every lane.
Status: contract baseline authored; prerequisite merge pending. The contracts below are frozen for this implementation sequence, not implemented product behavior. Downstream merges remain blocked until T00 merges.

## Verified public baseline

Verified on 2026-11-10 against public `main`, `7416e1408ecd9d11f58c638f0caf1c28e5fa6e3f`. GitHub reports PR #17 merged as `83f80fa91ea18c40832aa4084bb080f19e28370e`; comparison to this baseline is ahead by two commits, behind by zero, with that merge as the merge base. Preserve its repairs.

| Surface | Current source and invariant |
| --- | --- |
| Application/environment | `modules/common/Models/ControlPlaneModels.cs` already has `MinicloudApplication` and environment-owned `MinicloudApp`; `modules/api/Controllers/ApplicationsController.cs` and `modules/contracts/Contracts/AppDtos.cs` implement `/api/apps/{applicationId}/environments`. Do not recreate the old app model. |
| Schema | Latest migration is `20261110042000_ApplicationEnvironmentModel`, after `20261109161756_DatabasePlatform`. `modules/common/Data/MinicloudDbContext.cs` and `modules/common/Migrations/MinicloudDbContextModelSnapshot.cs` are the sole application schema baseline. |
| Browser/machine auth | `modules/api/Auth/ControlPlanePrincipal.cs` currently resolves Firebase and `mc_live_` keys; `modules/common/Auth/ActorIdentity.cs` shares the human-kind assumption. T30 replaces only human authentication. `ControlPlaneAuthorization.cs` and `ApiKeyScopes.cs` under `modules/common/Services/` remain the authorization authority. |
| CLI login | `modules/api/Controllers/CliLoginSessionsController.cs` and `modules/contracts/Contracts/CliLoginDtos.cs` already implement anonymous create/poll/exchange and authorized human approval. Keep the kebab-case `/api/cli-login-sessions` routes and DTOs. |
| Deployments | `DeploymentDtos.cs`, `AppDtos.cs`, `DtoMapping.cs`, `AppSummaryReader.cs`, `DeploymentPreparationsController.cs` and `DeploymentsController.cs` currently expose opaque IDs. T60 adds numbers at every creation/projection boundary, not a second runtime identity. |
| Distribution | `.github/workflows/cli-release.yml` publishes four CLI binaries; `.github/workflows/customer-deploy.yml` and `install.sh`/`install.ps1` download them. `modules/cli/Minicloud.Cli.csproj` targets .NET 10, references `modules/contracts`, and currently declares `3.1.0`; tags already include `v4.0.0`. Do not infer the next CLI version from the project default. |
| Packaging | `compose.self-host.yml` and existing API/worker/dashboard Dockerfiles are source-build assets. `modules/dashboard/app/core/config/runtimeConfig.ts` currently embeds Firebase/build-time values. Universal runtime configuration and the server channel do not exist yet. |

These are source observations, not live inventory, CI results or release qualification.

## Frozen integration decisions

- T20 uses canonical Git acquisition with verified full HEAD, not archive acquisition. Initial default: `7416e1408ecd9d11f58c638f0caf1c28e5fa6e3f`. Keep `MINICLOUD_VERSION`, `MINICLOUD_INSTALL_DIR` and the `minicloud_version` reusable-workflow input; the supported selector becomes a full 40-character lowercase SHA or an exact `vMAJOR.MINOR.PATCH[-prerelease]` CLI tag resolved once to a full commit. Verify membership in canonical `main` and fail closed if a selected tag moves during acquisition. Reject branches, `latest` and `server-v*`. The existing `MINICLOUD_REPO` override accepts only `muniventures/minicloud`; reject noncanonical overrides explicitly rather than silently building untrusted source.
- Use self-contained, untrimmed publish for the explicitly selected host RID, preserving `minicloud`/`minicloud.exe`. Building needs .NET 10 SDK and Git on the workstation/CI runner; running the installed output needs no separately installed .NET runtime. Preserve historical releases. T80 replaces the initial pin with an actual merged final-client revision; it is not the final candidate pin.
- SHA builds set `Version=0.0.0-source.sha<first-12-sha>`; tag builds use the tag version without `v`. Both set `SourceRevisionId` to the full SHA and include it in informational version output. A SHA identifies source, not a claim of compatibility with the final server.
- Server tags are `server-vMAJOR.MINOR.PATCH[-prerelease]`, with `make_latest=false`. The exact-release installer URL is `https://github.com/muniventures/minicloud/releases/download/server-v<VERSION>/install-server.sh`. Bundle names are `minicloud-server-<VERSION>.tar.gz`, `server-manifest.json`, `checksums.txt` and `checksums.txt.sigstore.json`. These are reserved names, not claims that assets are published.
- Optional stable discovery uses a separate `server-channel` release's `stable.json`, never `releases/latest`. Its `{schemaVersion:1, releaseTag, manifestSha256}` is only a discovery hint: verify the exact release's signed checksum/manifest binding before mutation. Exact release selection works without the channel.
- T40 signs checksums using GitHub Actions OIDC/Sigstore keyless signing. Trust requires the public Sigstore production trust root, issuer `https://token.actions.githubusercontent.com`, and exact certificate identity `https://github.com/muniventures/minicloud/.github/workflows/server-release.yml@refs/tags/<selected-server-tag>`, plus transparency/inclusion verification. A tag, adjacent checksum or digest alone is not publisher verification. The verified manifest binds bundle checksum, source SHA and every OCI digest; verify image provenance against the same source/workflow. Never accept a wildcard workflow/repository identity.
- T40's manifest has `schemaVersion:1`, `serverVersion`, `releaseTag`, `sourceRevision`, `architectures` (`linux/amd64`, `linux/arm64`), digest-pinned `images`/`dependencies`, `applicationMigration`, `identitySchemaVersion`, `configurationSchemaVersion` and `cliCompatibility:{protocolVersion,minimumVersion,maximumExclusiveVersion,sourceRevisions}`. Compatibility accepts an explicit source SHA or the declared semantic-version interval, AND the matching protocol. T80 records real tested versions/revisions; T00 invents no final range or digest.
- `/api` is the only first-party API namespace; `/health` remains the existing non-product readiness route. `GET /api/instance` retains `product`, `protocolVersion:1`, `appDomain`; T40 adds `serverVersion`, `sourceRevision`, `cliCompatibility` using the manifest shape. T20 checks advertised compatibility when present; T80 requires final metadata. This sequence introduces no `/v1`, auth fallback or route aliases. T60's breaking customer-identifier cutover requires coordinated server/client rollout and final compatibility metadata.

## Migration reservations and exclusive files

Each owner generates its own forward migration/designer from merged prerequisites, preserves historical migrations and hands off a clean snapshot. Reserved identifiers are ordered future schema slots, not existing migrations:

| Owner | Migration ID | Exclusive shared files / handoff |
| --- | --- | --- |
| T30 | `20261110100000_IdentitySessionsBootstrap` | `modules/common/Models/ControlPlaneModels.cs`, `modules/common/Data/MinicloudDbContext.cs`, `modules/common/Migrations/MinicloudDbContextModelSnapshot.cs`, `modules/common/Auth/ActorIdentity.cs`, `modules/api/Auth/ControlPlanePrincipal.cs`, `modules/common/Services/ControlPlaneAuthorization.cs` and `ApiKeyScopes.cs`; hand schema/auth files to T50 after merge. |
| T50 | `20261110110000_EnvironmentSshAccess` | Same model/context/snapshot after T30; `modules/contracts/Contracts/AppDtos.cs`, new `SshAccessDtos.cs`, `ProviderDtos.cs`, `modules/common/Services/DtoMapping.cs`, `ApplicationsController.cs`, `ProvidersController.cs` and provisioning/provider adapters. Hand model/context/snapshot, `AppDtos.cs`, `DtoMapping.cs` to T60 after merge. |
| T60 | `20261110120000_DeploymentNumbers` | Same schema files after T50; `DeploymentDtos.cs`, `AppDtos.cs`, `DtoMapping.cs`, `AppSummaryReader.cs`, deployment lifecycle/preparation/asset services, deployment controllers and `modules/api/Providers/IConsoleUrlProvider.cs`/`ConsoleUrlProvider.cs` as needed. |

T10 has no schema reservation. If a demonstrated defect needs one, stop and request a new slot and serialized handoff in the tracker; never race T30's snapshot. T10 also releases implicated deployment-controller/worker files before T50/T60 edit them. A task may proceed in other exclusive files while awaiting that handoff, but may not merge broken consumers.

| Shared surface | Exclusive sequence |
| --- | --- |
| Dashboard routing | T31 owns `modules/dashboard/app/routes.tsx` and `app/core/routing/dashboardRoutes.ts` for auth; T42 receives them after T31 merge for setup; T61 receives them after T42 merge for number routes. T51 stays out of these files. |
| Dashboard auth/config | T31 owns `app/core/auth/`, account API client and central credential/CSRF transport; T41 alone owns `app/core/config/runtimeConfig.ts`, public startup config and image plumbing. Use T30/T31's frozen interface below without concurrent edits. |
| App/environment dashboard | T51 owns `app/modules/Apps/` SSH forms/models, then T61 receives shared detail/history files. T42 owns setup/onboarding guidance only, not provider/app form rewrites. |
| CLI | T20 owns distribution workflows/installers/docs, not command partials. T52 owns `modules/cli/Commands/CliApplication.Apps.cs`, `.Environments.cs`, shared argument help in `CliApplication.cs`, `modules/cli/Api/MinicloudApiClient.cs` and related models/tests, then hands shared client/command files to T62. T30 publishes CLI auth contracts without editing these files. |
| Images/install | T40 owns new `.github/workflows/server-release.yml` and server manifest/bundle wiring; T41 owns existing Dockerfiles and `compose.self-host.yml`, then hands build changes to T40 and maintenance/Compose assets to T43, then T44. |
| Release docs/metadata | T20 owns CLI delivery docs and reusable-workflow callers; T43 owns server installation docs; T44 owns recovery additions; T80 receives final pins/compatibility only after all implementation merges. T00 owns this baseline and the central tracker. |

Later tasks add no columns/DTO fields owned by a predecessor without recording the handoff. New task-specific files are owned by that task; there is no permission to rewrite another lane's files merely because they share a directory.

## Deliverable

- Confirm `main` includes PR #17. Inventory current API/DTOs, migrations, release workflows and the completed application/environment model; do not recreate them from stale private snapshots.
- Adopt T20's source-only CLI path. Keep server installation free of a source checkout or build SDK on the control-plane VPS. Document separate server and CLI versions, exact source revision selection, release trust and canonical `/api` routes.
- Freeze T30's sessions/bootstrap contract, T50's environment SSH selection and T60's organization/date deployment-number contract before their consumers implement against them. API-key scopes, machine tokens and opaque runtime/operation identities retain their existing authorization boundaries.
- Record the exact public DTO/route contracts in the corresponding task files and assign migration numbers/owners. Keep changes additive within this implementation sequence where necessary for independently building PRs; do not add long-term dual routes, dual auth or compatibility layers.
- Assign exclusive ownership for `ControlPlaneModels.cs`, `MinicloudDbContext.cs`, its model snapshot, shared DTO/mapping files, dashboard route/config files and CLI command partials. The plan serializes shared backend model writers: T30 → T50 → T60.
- Confirm server installer asset/channel names are independent of the old CLI `install.sh`, and no workflow discovers a server release through repository-wide `releases/latest`.

## Acceptance

Cloud-agent tasks reference public in-repository contracts and identifiable files. There are no unresolved CLI binary/source contradictions, guessed route aliases, overlapping model/snapshot writers or hidden private-file prerequisites. Tests/builds for any contract code included in T00 are reported in its PR. This is not a live inventory, release publication or hosted authorization task.

## Issue prompt

Implement T00 from this file and the [feature overview](../1.pre-v1-release.md). Verify the shared contracts against current public source, update the affected task contracts without expanding scope, and submit one prerequisite PR. Do not start the downstream implementations or perform hosted actions.
