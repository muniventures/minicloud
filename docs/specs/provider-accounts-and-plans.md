# Organization provider accounts and named plans

Status: implemented locally; hosted rollout and live provider qualification are separate.
Date: 2026-11-08.

## Outcome and confirmed requirements

An organization can configure several cloud providers at the same time, including several accounts or credential scopes of the same provider. Each account has its own credentials in OpenBao and its own user-configured plan mappings. A deployment references only a Minicloud plan name. Minicloud resolves the account, region, and provider SKU internally.

Confirmed with the user:

- Each organization manages its own providers and plans.
- Each plan selects exactly one provider account. There is no automatic fallback to another account when quota is exhausted.
- A new plan starts with a UUID as its name. The user can rename it and reference the chosen name from the CLI or `minicloud.yml` without specifying a provider.
- Plan names must be unique for the user. With organization ownership, this specification interprets that as unique within the selected organization, across all its provider accounts. Names can repeat in another organization.
- This work produces a specification before implementation.

Example: create `Hetzner primary`, `Hetzner secondary`, and `Vultr backup`. Add plan `small-primary` to the first account and `small-secondary` to the second, even if both map to the same Hetzner SKU and region. Deploy one app with `small-primary` and another with `small-secondary`; each uses the explicitly selected account. When the first account reaches an IP quota, select `small-secondary` for a new, unprovisioned environment. Existing environments remain on their original account.

## Pre-implementation baseline: verified evidence

Paths below are relative to the product repository. These observations describe the working tree inspected before implementation, including its then-current line references. The implementation changes those paths; unrelated concurrent changes remain outside this specification.

| Current behavior | Source |
| --- | --- |
| VPS provider types are Hetzner, DigitalOcean, and Vultr. Cloudflare is a separate DNS integration. | `modules/common/Providers/CloudProvider.cs:3`; `modules/common/Providers/ProviderServiceCollectionExtensions.cs:46` |
| Clients resolve by provider type, with one global options object per type. Several types can coexist, but multiple accounts of one type cannot be selected. | `modules/common/Providers/ProviderServiceCollectionExtensions.cs:15`, `:19`, `:51`; `modules/worker/Program.cs:84` |
| Provider-specific account settings also exist: DigitalOcean project ID and Vultr default OS ID. | `modules/common/Providers/DigitalOcean/DigitalOceanOptions.cs:3`; `modules/common/Providers/Vultr/VultrOptions.cs:3` |
| Apps persist a fixed `AppPlan` enum. A switch maps its codes to global provider/region/SKU settings, including a default fallback. | `modules/common/Models/ControlPlaneModels.cs:59`, `:320`; `modules/common/Services/ProviderPlanMapping.cs:25` |
| API DTOs expose string plans, but creation and environment services parse them as enum codes. App update can silently ignore an invalid plan. | `modules/contracts/Contracts/AppDtos.cs:5`; `modules/api/Controllers/AppsController.cs:68`, `:256`; `modules/api/Services/AppEnvironmentService.cs:23` |
| Branch creation hardcodes the Vultr `P0V0` plan. | `modules/api/Services/AppBranchService.cs:47` |
| Portal plans, descriptions, and prices are hardcoded; no Providers navigation exists. | `modules/dashboard/app/modules/Apps/CreateAppModule.tsx:15`; `modules/dashboard/app/modules/Apps/AppEnvironmentsPanel.tsx:15`; `modules/dashboard/app/models/controlPlane.ts:101`; `modules/dashboard/app/core/routing/dashboardRoutes.ts:22` |
| CLI deployment configuration identifies an app; the control plane currently owns its plan selection. Environment selection happens before deployment preparation. | `docs/cli.md:107`; `modules/cli/Commands/CliApplication.Deploy.cs:104` |
| Worker provisioning reads the app plan, resolves a type-only client, and reuses registered servers. Discovery searches the selected client's scope by generated server name. | `modules/worker/Pipeline/Steps/ResolveProvisionServerStep.cs:71`, `:93`, `:108` |
| Server and runtime records have provider type and external resource ID, but no provider-account identity. Deletion groups targets by type and ID. | `modules/common/Models/ControlPlaneModels.cs:264`; `modules/common/Models/RuntimeModels.cs:5`; `modules/worker/Deletion/AppDeletionProcessor.cs:99` |
| Volume records have no account binding. External volume IDs are globally unique in the current database mapping. Volumes survive app deletion. | `modules/common/Models/AppStorageVolume.cs:3`; `modules/common/Data/MinicloudDbContext.cs:52`; `modules/common/Services/AppStorageService.cs:19` |
| The secret abstraction returns store/path/version references, but its write request and OpenBao namespace are app/service-specific. OpenBao reads currently ignore the reference version. | `modules/common/Services/ISecretStore.cs:3`; `modules/common/Services/OpenBaoSecretStore.cs:15`, `:29`, `:101` |
| Authorization already follows organizations, roles, and API-key scopes, but has no provider/plan permissions. | `modules/common/Services/ControlPlaneAuthorization.cs:10`, `:43`, `:60` |
| Build-target identity, runtime registration, and DNS ownership checks also compare provider type and resource ID. | `modules/common/Registry/ImageBuildTargetService.cs:26`; `modules/worker/Pipeline/Steps/RuntimeAgentDeployment.cs:79`; `modules/worker/Pipeline/DomainDnsReconciler.cs:38`; `modules/api/Controllers/RuntimeController.cs:258` |

Conclusion: adding a second API key alone is insufficient. Account identity must become part of resource ownership throughout the lifecycle, and the fixed enum must become an organization-owned plan catalog.

## Scope

Include account CRUD, credential replacement, account validation, custom plan CRUD, portal management, name-based CLI/YAML selection, account-aware provisioning and lifecycle operations, and migration of existing installations.

Keep the supported VPS types to Hetzner, DigitalOcean, and Vultr. The type selector is a server-owned allowlist, not a user-entered plugin or arbitrary API endpoint. Adapter capability metadata must continue to identify Hetzner-only durable storage; adding multiple accounts does not add volume support to other providers.

Exclude automatic quota balancing, account pools, cross-account failover, cross-account migration, server resizing, provider billing, automatic price comparison, and new provider adapters. Cloudflare DNS configuration, control-plane hosting, and the deployment SSH private key remain separate installation concerns.

## Portal workflow

Add **Providers** to the organization navigation.

The list shows account display name, provider type, enabled state, credential-validation result and timestamp, and plan count. Two Hetzner accounts appear as two independent entries. An empty organization is guided to add an account and then a plan before creating an app.

**Add provider** asks for a name, one supported provider type, an API token/key, and the non-secret settings required by that adapter. DigitalOcean project selection belongs to this account's settings. Credentials are write-only. No provider token is returned to the browser after submission.

**Provider detail** offers metadata editing, credential replacement, an explicit read-only connection check, disable/enable, and its plans. Connection checks must not create a billable server or volume. Store their result as an observation, not a guarantee that the next allocation has quota. Invalid or unavailable credentials are shown distinctly from a disabled account.

**Add plan** starts with an editable UUID name. The user configures the provider SKU, region, and any required supported image/OS and Linux build-platform mapping. Offer adapter defaults where safe; do not require users to enter raw cloud-init or credentials in a plan. Catalog dropdowns may assist if implemented, but manual exact identifiers must remain possible and verified before provisioning. Do not make catalog discovery a prerequisite for this feature.

**Edit plan** allows renaming and enabling/disabling at any time. Proposed safety rule: provisioning fields can change until the first durable provisioning reservation. After that, account/region/SKU/image/platform are immutable; offer duplicate-as-new-plan to change them. This prevents a plan edit from changing the meaning of a retry or silently relocating an existing environment. This rule is a proposal, not an additional user-confirmed requirement.

App and environment selectors load enabled plans dynamically. Show plan name with the account name, provider, region, and SKU as supporting information. No fixed prices are shown as authoritative; omit pricing unless independently sourced and explicitly labeled. No hardcoded default provider plan remains.

Provider type and account/project scope cannot be changed after resource reservations exist. Account renaming and token rotation are allowed, but token replacement must remain in the same underlying cloud account/project scope. Moving to a different scope requires a new provider entry.

## Names and persistent identities

Separate mutable user-facing names from stable internal IDs:

| Entity | Proposed persisted fields and constraints |
| --- | --- |
| `ProviderAccount` | Stable `Id`, `OrganizationId`, display `Name`, allowlisted `ProviderType`, non-secret adapter settings, enabled state, credential reference/generation, validation metadata, optimistic concurrency revision, timestamps. |
| `ProviderPlan` | Stable `Id`, `OrganizationId`, `ProviderAccountId`, mutable `Name`, normalized name key, region, SKU, supported image/OS settings, Linux platform, enabled state, provisioning-lock marker, optimistic concurrency revision, timestamps. |
| Plan name claim | Organization, normalized name, original plan ID, and current/retired state. Unique by organization/name; retain retired claims to prevent stale YAML from selecting a different plan. This is a safety proposal. |
| App/environment | `PlanId` foreign key replaces enum-backed `Plan`; provider is derived from the plan. Keep the existing per-environment app identity and isolation. |
| Provisioning reservation/deployment | Organization/app, plan ID and revision, provider-account ID, provider type, immutable placement snapshot, stable discovery/idempotency identity, and allocation outcome. Persist before external allocation. Reuse the existing deployment/ownership lifecycle where possible rather than adding a parallel queue. |
| Server and runtime registration | Provider-account ID in addition to existing resource identity; server also retains the placement snapshot that created it. |
| Retained volume | Organization ID, provider-account ID, provider type and external volume ID in addition to current app/name/region fields. Retain ownership independently of the app row. |

When plan creation omits a name, the server generates a UUID string as its initial name; the portal can display it before saving. The stable internal ID is separate and is not changed when that name changes. Users can choose human names, including spaces and Unicode. Normalize Unicode, trim outer whitespace, reject empty/control-character names, and cap length at 128 characters. Proposed uniqueness is case-insensitive after normalization, enforced by a database unique constraint on `(OrganizationId, NormalizedName)` in the name claims. It is not scoped to a provider account. Conflicts return an explicit validation error.

Names are resolved in the authenticated organization, never globally. Use JSON bodies or query parameters for arbitrary names rather than embedding them in URL path segments. Public management routes use stable IDs.

Rename behavior: existing app/environment bindings continue using `PlanId`; responses display the current plan name. Checked-in YAML and shell commands containing the old name must be updated. There is no implicit alias or fallback to the UUID/internal ID after renaming. Retire the old name claim: lookup returns a renamed/retired-name error, never a different plan. Another plan cannot take that name, including after the original plan is deleted; the original plan can reclaim its own former name. Unknown names fail clearly before provisioning. Claims and plan rename commit atomically.

Foreign keys and service validation must enforce that app, plan, account, reservation, server, runtime, and retained volumes belong to the same organization. Account IDs are never accepted as proof of access.

## CLI and YAML contract

Users specify only the plan name:

```yaml
app: example-api
appId: app_example
plan: "Hetzner secondary small"
services:
  api:
    image: example/api:1
    port: 8080
    public: true
```

Proposed commands:

```sh
minicloud plans list
minicloud deploy --plan "Hetzner secondary small"
minicloud deploy --environment staging --plan "Hetzner secondary small"
minicloud environments create staging --plan "Hetzner secondary small"
```

- Add optional top-level YAML `plan` and optional deploy `--plan`. Precedence is CLI flag, then YAML, then the selected environment's saved plan. Omitting both preserves existing configurations and uses the saved binding.
- Resolve the target app and environment first, then resolve the plan name within that app's organization. Preserve existing sole-environment auto-selection and required selection when several environments exist.
- An explicit plan selects and persists the binding for an unprovisioned environment, atomically with admission under the app's deployment lock. Do not mutate the binding on a failed validation or before checking active preparations and provisioning history.
- A provisioned environment accepts an explicit name only when it resolves to the same saved plan ID. A different ID returns `plan_change_requires_migration`; ordinary deploy never moves or resizes a server. Even equivalent SKUs on another account are different placements.
- A reservation with an unknown allocation outcome also prevents changing plans. A definite rejection with no external resource and no retained data can permit a later explicit selection after reconciliation.
- Resolve and validate before local image builds, image publication, artifact upload, or provider allocation. Include the resolved placement in build-target identity so preparation and worker execution cannot choose different accounts or architectures.
- Add `minicloud plans list` for discoverability. It returns current names and safe mapping metadata. `init` can show the catalog and save the chosen name; it must preserve existing app identity and YAML content.
- Provider credentials and provider selectors do not belong in customer YAML. The provider is determined entirely by the resolved plan.
- Each environment retains its own plan. A newly created branch uses an explicit plan if provided, otherwise inherits its parent's plan ID at creation. Existing branch/environment bindings are unchanged by later parent edits. Remove the current unconditional Vultr branch plan.

## API contract

Proposed routes, following existing `/v1` organization authorization patterns:

| Route | Behavior |
| --- | --- |
| `GET /v1/provider-types` | Supported VPS types, safe credential-field descriptions, adapter defaults and capabilities. No secrets or organization configuration. |
| `GET/POST /v1/providers?organizationId=...` | List/create accounts within the authorized organization. Creation accepts a write-only credential object and adapter settings. |
| `GET/PATCH/DELETE /v1/providers/{id}` | Read safe metadata, edit metadata/state, or remove an unused account. |
| `PUT /v1/providers/{id}/credentials` | Replace credentials with optimistic concurrency and scope checks; returns only safe metadata. |
| `POST /v1/providers/{id}/validate` | Perform a read-only credential/scope check and return sanitized results. |
| `GET/POST /v1/plans?organizationId=...` | List/create organization plans; optional account filter for the provider detail page. |
| `GET/PATCH/DELETE /v1/plans/{id}` | Read/edit/retire plans subject to uniqueness, usage and reservation rules. |

App creation, app update, environment creation, branch creation, and deployment admission accept a plan name where selection is needed. Reuse the existing `plan` string field where practical; define its new meaning explicitly as the organization plan name. Responses include `planId`, current `plan` name, and safe account/type/placement information where relevant. Internal worker input uses stable IDs and snapshots, never name lookup.

`GET /v1/plans` supplies name resolution and discovery; a dedicated global-name route is unnecessary. Request schema and OpenAPI must distinguish the provider's SKU from the user's Minicloud plan name.

Return explicit errors for unknown/duplicate plan names, unsupported provider types, cross-organization references, disabled accounts/plans, invalid placement, immutable mapping changes, stale revisions, unavailable credentials, unresolved resource ownership, and a requested change to an existing environment's placement. Never substitute a global default or silently ignore an invalid name.

Introduce organization permissions for reading provider metadata, managing accounts/credentials, reading plans, and managing plans. Owner/Admin can manage providers and plans; Developer/Viewer can read safe plan metadata, with Developer retaining existing app/deployment permissions. Provider metadata reads never include secrets or secret paths. Add separate API-key scopes for these actions; existing deployment keys do not acquire provider credential management implicitly. An API key may select an authorized organization plan as part of its existing app/deploy authority without permission to read credentials.

## Secret storage and rotation

Use OpenBao through the existing secret-store abstraction. Add an organization/provider-account namespace rather than inventing a fake app/service:

```text
env/{installation-environment}/orgs/{organizationId}/providers/{providerAccountId}/credentials/{generation}
```

Persist only secret-store references, credential generation, and sanitized validation metadata in PostgreSQL. The secret payload contains the adapter's token/key. Non-secret settings such as region, SKU, project identity, and OS are ordinary configuration. OpenBao bootstrap credentials remain installation secrets.

Credential generation identifies an immutable secret path. Operations capture that generation once; do not let one operation read different tokens on successive HTTP requests. If versioned paths are used instead, implement exact version reads first: current `GetAsync` ignores `StoredSecretReference.Version` and is insufficient for a pinned credential contract.

For replacement: write the candidate generation, validate authentication and scope without allocation, then atomically switch the account reference using its revision. A failed write/validation leaves the prior reference intact. Reconcile abandoned candidate writes and delayed old-generation cleanup across process failures; do not delete a generation still used by active operations. Do not rely on a database transaction to make an external OpenBao write atomic.

Where an adapter can observe stable cloud account/project identity, persist and compare it on rotation. If scope identity cannot be proven by a read-only adapter check, block rotation for an account with resources and require explicit scope reconciliation; accepting a token for a different account must never redirect existing resource operations. Determine each adapter's exact scope-validation mechanism from official documentation during implementation.

Implemented qualification boundary: existing managed servers/volumes are verified by their exact registered identities through the account's adapter. Rotation is serialized with account reservation and plan creation. If the account has no verifiable resources but has ever had a provisioning reservation, credential replacement remains blocked, including after canceled preparations/definite rejection; a canceled worker may still hold the old credential for discovery. Use another provider entry for a new placement or reconcile an original resource. This conservative limitation avoids claiming an account/project identity that the current adapters do not expose.

OpenBao unavailability fails closed before provider dispatch. Do not fall back to environment-variable credentials or another account. Redact credential payloads, authorization headers, raw provider/OpenBao bodies, and secret paths from responses, audit records, deployment events, and logs. Audit only actor, organization, account/plan IDs, action, revision, and sanitized result. Provider keys never reach app containers, build artifacts, runtime-agent config, or deployment YAML.

## Provisioning and resource lifecycle

1. Authenticate, resolve the environment and selected plan, check organization ownership, capabilities and placement, and acquire the existing deployment/admission ownership. Distinguish registered-server reuse from new selection/allocation before enforcing enabled state: disabled plans/accounts reject new selection/allocation but allow reuse of the same saved binding as described below. An explicit name resolving to that same ID is reuse, not a new selection.
2. Persist the selected IDs, plan revision and placement before external allocation or local build preparation that depends on placement. Mapping edits and reservation creation must serialize so the snapshot and immutable-mapping lock agree.
3. Resolve a client with organization/account identity and an operation-scoped credential generation. Keep shared transports separate from per-account authorization; never mutate shared default authorization headers across concurrent operations.
4. For a registered server, use its persisted account and resource ID for status, discovery, bootstrap, storage and deletion. Verify consistency with the reservation; never re-derive its account from the current plan or a global default.
5. For a new server, discover/create only within the reserved account scope, using stable app/environment identity and available ownership labels/tags. A timeout or ambiguous provider response preserves an unresolved reservation. Reconciliation checks the same account before another create; it never tries a different account automatically.
6. Persist server account, provider resource ID, region and effective placement. Carry account identity into runtime registration and every subsequent ownership comparison.

Canonical external resource identity is `(ProviderAccountId, ProviderResourceId)`; provider type is verified against the account. Include organization ownership in authorization queries. Replace type-only comparisons in provisioning, deletion, runtime token registration, custom-domain verification, DNS reconciliation, runtime status ingestion, storage operations, and build-target identity. Preserve existing cross-app conflict rejection inside each account scope. Do not merge the legitimate server and runtime representations of the same resource accidentally.

Prevent duplicate registration of the same actual cloud scope where it can be identified; otherwise do not assume two configured tokens represent distinct accounts. Resource adoption must verify stable app ownership evidence, including across entries that may address the same scope. An unresolved alias/scope conflict blocks adoption or deletion rather than weakening current ownership checks.

Change volume identity indexes from globally unique external IDs to account-scoped IDs, and update runtime indexes consistently. Durable volumes must match the server's account and region. Preserve current refusal to replace a missing/unresolved volume with an empty disk; account changes never create a replacement disk as a shortcut.

Quota failure is attributed to the selected account. Existing servers remain manageable even when that account cannot allocate another IP. The UI suggests selecting another plan for a new/unprovisioned environment; it does not claim a quota failure means credentials are invalid.

## Disable, deletion, and existing placements

- Disabling an account or plan prevents new allocations/selections. Existing deployments can reuse their registered server and mount existing volumes. New resource allocation, including a new volume on that account, is blocked while the account is disabled; plan disablement alone does not block normal maintenance of an already provisioned environment.
- Disabling never removes credentials or prevents status, reconciliation, server deletion, or other required cleanup of existing resources. Invalid/revoked credentials can still make those operations fail, with a clear account-specific error.
- A referenced plan cannot be hard-deleted; disable it. A plan with no app, reservation, server or retained-resource references can be deleted. Retain its name claims as tombstones so old configuration cannot select a different plan later.
- An account cannot be deleted while plans, environments, active/unresolved reservations, registered servers/runtimes, or retained volumes depend on it. Return dependency counts. Retained volumes survive app deletion and continue to protect the account reference.
- Account deletion never deletes cloud resources implicitly. Remove credentials only after dependency checks and durable deletion intent, with retryable OpenBao cleanup. Do not report full removal until cleanup completes.
- Existing environments cannot switch plan placement through app update or deploy. Cross-account migration and resize require a future explicit workflow that handles data, IPs, runtime identity, and routing.

## Migration and compatibility

1. Add accounts, plans, references, snapshots, and account-aware indexes without immediately dropping legacy enum columns. Back up PostgreSQL and OpenBao consistently. No provider resources are created by schema migration.
2. Before importing credentials, inventory which organization and cloud credential scope owns each existing server/runtime/volume. Existing global configuration does not prove organization ownership. Produce a non-secret import manifest; require explicit organization assignment where it cannot be established. Never copy one global account silently into every organization.
3. Provide a one-time operator import command that reads existing local secret configuration directly and writes it to OpenBao. Do not pass token values in command arguments, migration SQL, generated public configuration, or output. Include the non-secret DigitalOcean project/Vultr OS settings. Import is resumable and idempotent by explicit legacy-scope assignment, without storing token-derived fingerprints as public identifiers.
4. Import known configured legacy plan codes as named plan rows in the assigned organization. Preserve the exact effective mapping, including the old `P0` default. Legacy names may remain for compatibility; newly created plans default to UUID names. Use the existing effective settings, not today's assumed SKUs or prices. Missing credential configuration must not create apparently usable plans.
5. Backfill app/environment plan IDs and original account identity on servers, runtimes, and retained volumes. Include orphaned retained volumes and pending/failed allocation attempts. Inspect actual registered provider/region rather than assuming the app's current plan still describes its server. Any mismatch, ambiguous scope, duplicate cloud-scope alias, or unavailable secret is a blocking migration report; no default-account guess.
6. For old records whose SKU/image/platform cannot be recovered, record the value as unknown and allow safe reuse of the identified server. Do not invent a complete snapshot or permit replacement allocation from incomplete history.
7. Pause admissions and drain active deployments, preparations, deletion and reconciliation work for cutover. Apply/backfill and verify all bindings, then start account-aware API and workers together. Old workers must not consume new jobs. Block affected resources with unresolved binding rather than invoking the legacy type-only resolver.
8. Deploy and verify the hosted backend and migration before publishing a CLI that sends custom names/YAML plans. Legacy CLI fields can continue to carry imported plan names; old clients with hardcoded selectors remain limited to their known names and must upgrade for custom catalogs.
9. Remove automatic runtime use of global provider tokens and `AppPlan` mapping after cutover. Remove obsolete credential environment settings only after import/restore qualification. Update self-hosting, Compose examples, CLI docs and portal onboarding to create accounts/plans through the new workflow.

Rollback: before admissions reopen, restore the coordinated database/secret backup if migration fails. After account-aware allocations begin, old binaries are not a safe rollback target because they lack account identity; fix forward or use a reviewed export/rollback procedure. Do not roll back to a type-only worker while multi-account resources exist.

## Implementation sequence and affected files

1. **Catalog and schema:** add provider-account/plan entities and constraints; replace enum usage in `modules/common/Models/ControlPlaneModels.cs`, extend `RuntimeModels.cs`, `AppStorageVolume.cs`, `modules/common/Data/MinicloudDbContext.cs` and migrations. Preserve organization and retained-resource ownership.
2. **Credentials and client creation:** extend `modules/common/Services/ISecretStore.cs` and `OpenBaoSecretStore.cs`; add account credential lifecycle and account-aware client factory; change `modules/common/Providers/ProviderServiceCollectionExtensions.cs`, adapter constructors/options and worker registration. Reuse existing adapters/retry logic; no new package is required by this spec.
3. **API, policy and admission:** add provider/plan controllers/contracts, authorization actions/scopes and sanitized audit events; update `modules/contracts/Contracts/AppDtos.cs`, `modules/common/Validation/ControlPlaneValidation.cs`, `modules/api/Controllers/AppsController.cs`, `DeploymentsController.cs`, `modules/api/Services/AppEnvironmentService.cs`, `AppBranchService.cs`, and deployment preparation/lifecycle paths. Remove silent invalid-plan updates.
4. **Whole lifecycle:** update `modules/worker/Pipeline/Steps/ResolveProvisionServerStep.cs`, pipeline context, storage/runtime steps, `modules/worker/Deletion/AppDeletionProcessor.cs`, `modules/common/Services/AppStorageService.cs`, `CustomDomainVerificationService.cs`, `modules/worker/Pipeline/DomainDnsReconciler.cs`, `modules/api/Controllers/RuntimeController.cs` and `modules/common/Registry/ImageBuildTargetService.cs`. Search all remaining provider/resource comparisons before declaring migration complete.
5. **CLI and portal:** extend `modules/cli/Config/MinicloudConfig.cs`, loader/writer/validator and deploy/init/environment/branch commands; add plan discovery; update dashboard routes, API client, models and dynamic app/environment selectors. Add Providers/account-detail/plan forms. Preserve sole/multiple-environment selection behavior.
6. **Import and rollout:** deliver resumable operator import and mismatch reporting, migration fixtures, self-host examples and docs. Qualify API/worker cutover before CLI release and hosted browser acceptance. Hosted operations changes belong in `minicloud-services`, product changes in this repository.

## Acceptance and verification

| ID | Required evidence |
| --- | --- |
| A1 | One organization creates two Hetzner accounts and one each of Vultr/DigitalOcean; two plans with the same SKU/region but different accounts resolve independently. |
| A2 | A plan starts with a UUID name, is renamed to a Unicode/spaced name, and resolves from quoted YAML and CLI. A duplicate normalized name under another account in the same organization is rejected; another organization can use it. |
| A3 | Rename preserves internal plan IDs and existing bindings. The previous name gives an explicit renamed/retired-name error; another plan cannot claim it, even after deletion. The original plan can rename back. It never resolves to another provider or a global default. |
| A4 | CLI flag overrides YAML, YAML overrides saved plan, and omission preserves saved selection. Environment resolution precedes plan resolution and all build/upload/allocation work. |
| A5 | App/environment/branch creation uses organization plan names; new branches inherit the parent plan unless explicitly overridden. Existing branches retain their binding. |
| A6 | Concurrent mocked operations for two accounts of the same type send only their own authorization headers. Status/retry/delete uses the persisted original account even after metadata edits. |
| A7 | Identical external server/volume IDs in different verified account scopes do not collide. Cross-app ownership conflicts within one scope and duplicate-scope alias hazards still fail safely. |
| A8 | A quota rejection on account A never dispatches a request to B. A new unprovisioned environment can explicitly choose B; an existing server on A cannot silently move. |
| A9 | A mapping edit races reservation creation: exactly one consistent revision wins. A lost create response is reconciled in the same account without duplicate allocation or another-account fallback. |
| A10 | Credential values are absent from PostgreSQL, DTOs, logs, events, audit, YAML, artifacts and runtime config. OpenBao unavailability causes zero cloud dispatch. |
| A11 | Failed credential replacement preserves the old generation. Concurrent rotation/operations do not mix tokens. Candidate-write crash recovery and old-generation retention are tested. Wrong/unknown scope replacement cannot redirect existing resources. |
| A12 | Cross-organization reads/writes/selection fail, including crafted IDs. Owner/Admin manage accounts/plans; Developer deploys without receiving secrets; ordinary deploy keys cannot manage credentials. |
| A13 | Disabled accounts/plans block new selection/allocation as specified while registered-server reuse, status and deletion remain possible. Retained volumes block account deletion after app deletion. |
| A14 | Durable storage uses the server's account/region, rejects other scopes and unsupported providers, and preserves missing/ambiguous-volume protection. |
| A15 | A populated legacy database migrates without replacing servers/disks, preserves named environments and legacy plan names, and reports scope/placement mismatches and orphaned volumes instead of guessing. Re-running import creates no duplicate accounts/secrets/plans. |
| A16 | Portal supports account/plan add/edit/rename/disable, secret replacement and connection-check failures; app/environment choices come from the catalog, with empty/loading/error states and no static prices. |
| A17 | Registry preparation, runtime token registration, domain ownership, DNS reconciliation and deletion all reject inconsistent account bindings; build-target identity changes when effective placement changes. |

Implement targeted .NET, PostgreSQL migration, mocked provider HTTP, CLI config/precedence, and dashboard tests first. Existing starting points include `modules/tests/Minicloud.Tests/ControlPlaneCoreTests.cs`, `AppEnvironmentServiceTests.cs`, `AppBranchServiceTests.cs`, `ImageBuildTargetTests.cs`, worker provider/pipeline/deletion tests, and CLI deployment/config tests. Test actual PostgreSQL uniqueness and locking rather than relying only on an in-memory database.

Then run the affected .NET suites, dashboard tests/build, and CLI help/config validation following `README.md`. Disposable live qualification must separately demonstrate two distinct accounts of the same provider, a second provider type, reuse, credential rotation, a quota/error path, retained-volume safety, and deletion targeting. Live billable provisioning requires a separately authorized execution phase; local HTTP fixtures are not live provider evidence.

Specification verification: inspect source references, check examples against the proposed contract, confirm the acceptance matrix covers both confirmed requirements and migration/lifecycle risks, and leave existing working-tree modifications intact. No build, deployment, provider API call, commit or push is part of this specification task.

## Proposed defaults to review before implementation

- Organization-level uniqueness is the concrete interpretation of “unique for the user.”
- Names are case-insensitive after normalization; renames do not retain aliases, and retired names remain reserved against use by other plans.
- Provisioning mappings lock after their first reservation; duplicate the plan to alter a used mapping.
- A new branch inherits its parent plan unless the user selects another plan.
- Disabled accounts preserve management of existing resources but block additional allocation.

These defaults make the specification concrete and testable. The two architectural choices already confirmed—organization ownership and one account per plan—are settled.

## Local implementation evidence (2026-11-08)

Implemented the organization catalog and API/portal CRUD, OpenBao credential generations, account-aware server/runtime/volume lifecycle, immutable placement reservation, named CLI/YAML selection, explicit legacy import, and additive database migration.

Solution and dashboard builds passed. Final regression runs cover 393 distinct common/API cases, 188 worker cases, 281 CLI cases and 147 dashboard cases, all passing. Four optional real SSH/SFTP cases were skipped. PostgreSQL migration preservation, reservation/edit/rotation/deletion lock races, account-isolated HTTP requests, legacy import and packaged API/registry push/pull/restore/GC checks passed. EF reports no pending model changes.

Two registry checks initially hit the existing 15% free-disk reserve. The user authorized removal of unused Docker build cache; Docker reported 138.3 GB removed and the host subsequently had about 180 GiB free. Both checks then passed without weakening the reserve. The registry fixture's teardown was also corrected to remove and verify its per-run API/worker images.

No hosted deployment, CLI publication, paid provider allocation or live multi-account cloud acceptance was performed. The conservative empty-account credential-rotation boundary is documented above and in the user guide. No commit or push was made.
