# Application, environment, and service model

Status: implemented locally; hosted rollout and live qualification remain separate.
Date: 2026-11-09.

## Outcome

Minicloud presents one application for a product, with independently operated environments and services inside each environment:

```text
Organization
└── App: MuniPages
    ├── Environment: Production
    │   ├── api
    │   ├── editor
    │   ├── websites
    │   └── worker
    └── Environment: Staging
        ├── api
        ├── editor
        ├── websites
        └── worker
```

The example describes the intended hierarchy, not a verified inventory of deployed MuniPages resources. The Apps page shows MuniPages once. Production and staging are peers beneath it. Selecting an environment establishes the scope of every operational view and action.

Confirmed direction: use **App → Environment → Services**, give applications and environments distinct identities, make environment selection prominent, and preserve existing resources during migration. The detailed contracts below are specification decisions for implementation.

## Current implementation and cause

Verified against the product working tree:

| Evidence | Consequence |
| --- | --- |
| `modules/common/Models/ControlPlaneModels.cs`: `MinicloudApp` contains `ParentAppId`, `BranchName`, `EnvironmentName`, plan, domains, secrets, and deployments. | One entity represents an application, an environment runtime, and a branch runtime. |
| `modules/api/Services/AppEnvironmentService.cs`: a named environment is another `MinicloudApp`, named `{parent.Name} ({environment})`. | Environment identity leaks into application names. The root also serves as the initial environment. |
| `modules/dashboard/app/modules/Apps/AppsModule.tsx`: calls `listApps(..., true)` and classifies records using `parentAppId`. | Environment records appear alongside apps and can be classified as branches. |
| `modules/dashboard/app/modules/Apps/AppEnvironmentsPanel.tsx`: resolves sibling environments through a root app and links to `/apps/{environment-runtime-id}`. | Environment navigation looks like navigation between different apps. |
| `modules/cli/Commands/CliApplication.Deploy.cs`: resolves the root, selects a named environment, then substitutes its runtime app ID. | The CLI already supports the intended selection semantics, but transport identities mix both concepts. |

This is a domain and navigation correction. Filtering duplicate cards alone is insufficient.

## Terms and ownership

| Concept | Meaning | Owns |
| --- | --- | --- |
| App | A logical product or system, such as MuniPages. | Organization membership, stable ID and slug, display name, environment collection. |
| Environment | An independently configured deployment context, such as production, staging, or a preview. | Plan binding, runtime placement, service configuration, secrets, domains, storage associations, deployments, operational lifecycle. |
| Service | A named deployable component within one environment. | Service configuration, image/build settings, service secrets, routes, runtime observations, logs, and deployment participation. |

Use “App” in navigation and “Application” in explanatory text where useful. Do not introduce Project as an additional grouping layer. Do not use environment suffixes in app display names.

Application membership here means ownership by an organization; existing organization roles remain the authorization boundary. This feature does not add per-app or per-environment RBAC.

Control, delivery, and backup describe component responsibilities, not environments. They can be services within one environment even when hosted separately. Existing independently registered apps with those names are not automatically consolidated.

An environment is a logical isolation boundary, not a synonym for one VPS. Preserve the existing supported placement behavior. This work must not introduce shared networking, shared volumes, multi-server scheduling, automatic failover, or service movement as a side effect of grouping.

## Data model

Introduce an explicit application entity and an explicit environment entity. Internal class/table naming may follow repository conventions, but their responsibilities must remain separate.

### Application

- `id`: immutable application identity.
- `organizationId`: owning organization.
- `name`: editable display name.
- `slug`: stable organization-scoped identifier using existing app-slug validation.
- `defaultEnvironmentId`: nullable reference to an environment of this application; used for dashboard navigation only.
- Creation/update timestamps and application deletion lifecycle.

An application has no plan, database mode, domain collection, latest deployment, or runtime health of its own. Summaries explicitly aggregate environments.

### Environment

- `id`: immutable environment identity.
- `applicationId`: required owner.
- `name`: editable user-chosen name; retain current validation: 1–63 ASCII letters, digits, dashes, or underscores, starting with a letter or digit.
- Names are unique within an application, ignoring case; preserve display casing.
- `kind`: `persistent` or `preview`. Production and staging are names, not special kinds.
- Nullable `sourceBranch` and `sourceEnvironmentId` for preview provenance; neither implies ongoing inheritance.
- Existing plan/provider binding, database configuration, placement/runtime references, timestamps, and deletion status/error.
- Stable environment runtime routing identity; persisted migration metadata remains internal.

Production has no structural privilege over staging. Either may be created first, renamed, or deleted independently. Application creation offers one initial persistent environment, named `production` by default, and creates both records atomically. Creation does not itself provision infrastructure.

Zero-environment applications are valid after environment deletion. They display an explicit empty state and cannot deploy until an environment is created. Changing the dashboard default does not change CLI deployment selection.

### Services and operational resources

Service identity is scoped by environment. The same service name in production and staging identifies different service instances. Environments can have different service sets and configurations; creating one does not establish automatic synchronization.

Retain the current authoritative service configuration and deployment materialization mechanisms; this specification does not require a second service registry or a new service CRUD system. Every service lookup and operation must resolve one environment explicitly.

Deployments target exactly one environment and one or more services within it. Domains, secret references, storage associations, runtime registrations, build targets, operation locks, cancellation, and deletion jobs must resolve the same environment identity. Organization-owned provider accounts and plans remain organization-owned; the selected plan belongs to the environment binding.

No implicit shared secrets, database, storage, or network is created between environments. New persistent environments begin with empty secrets. Preview creation preserves the existing explicitly documented branch provisioning/secret-copy behavior within their selected source environment; do not silently apply that behavior to persistent environments. Existing materialized secrets and resource bindings remain unchanged.

## Dashboard

### Apps list

Show one table group per application, following the approved [Apps and operations UX](apps-operations-ux.md). Each contains the app name, slug, environment count, and compact environment subrows: environment name, latest deployment result and completion/start timestamps where available. Columns are App, Environment, Latest deployment and Actions. Do not display Plan or Runtime columns or filters in the Apps list. Plans and runtime observations belong inside the selected environment. Persistent environments appear first; previews may be collapsed behind a count.

Do not show one app-level plan, database, runtime badge, or deployment result as if it describes every environment. Inside an environment, deployment success and runtime health remain separate observations. Missing/stale runtime observations must remain visibly unavailable/stale.

Search includes app name/slug, environment names, and associated domains, but returns each application once. Environment-based filters match an application when at least one environment meets all selected environment filters; show which environment matched. App counts count applications. Remove the current main/branch app filter; preview filtering uses environment kind.

### Application and environment navigation

Canonical browser routes:

```text
/apps
/apps/{applicationId}
/apps/{applicationId}/settings
/apps/{applicationId}/environments/{environmentId}/{tab}
```

The app landing route opens its dashboard default environment, or the environment-creation empty state if none exists. Initial default is the first created environment. On default-environment deletion, select the oldest remaining persistent environment, then the oldest preview, with ID as a deterministic tie-breaker; use null if none remain.

The operational page header displays:

```text
Apps / MuniPages / Production
MuniPages     [Production ▾]
```

Place the environment selector above the tabs and service content. It lists environment names, kind where relevant, runtime observations, and creation/management links permitted by existing roles. It is keyboard accessible. Switching preserves the selected tab when supported and replaces all environment-specific data. Cancel or discard stale requests so a previous environment's results cannot render under the new header.

Operational tabs are Overview, Services, Storage, Domains, Secrets, Deployments, Console, and Logs. Plan/provider/database information describes the selected environment. Application settings contain app metadata, environment management, and app deletion. Environment settings contain rename, plan selection under existing placement restrictions, dashboard-default selection, and environment deletion. Move the current below-content environment panel into these surfaces.

Mutating controls, confirmation dialogs, console connections, and deployment progress identify both app and environment. Environment switches close active console sessions; an in-flight operation remains bound to its original immutable target. Never retarget a pending operation from a selector change.

Support narrow 320–390px screens: app/environment context remains visible, the selector fits the viewport, and primary navigation does not require horizontal scrolling. Use a compact menu or wrapping layout for tabs.

### Creation and deletion

“Create app” asks for app name/slug and the initial environment name, plan, and supported database selection. “Add environment” asks for name, plan, and supported database selection; it does not clone production data or secrets. Both use the organization plan catalog and explain empty/error/disabled-plan states.

“Delete environment” names one target and affects only its resources. “Delete app” lists every affected environment, including previews, and deletes them through existing lifecycle safeguards. Preserve retained-volume behavior and explain retained billable resources. Failed environment deletion remains visible under its app with its error and retry action. Application deletion completes only after all required child cleanup completes. Reject new environments/deployments while application deletion is active; worker execution rechecks lifecycle state.

## API contract

User correction on 2026-11-10: this prelaunch product has no API compatibility requirement. There is one unversioned first-party API. Do not add versioned aliases, mixed clients, or legacy identity resolvers.

| Endpoint | Responsibility |
| --- | --- |
| `GET/POST /api/apps` | List applications with bounded environment summaries; create an application and initial environment. |
| `GET/PATCH/DELETE /api/apps/{applicationId}` | Application metadata, default selection, and lifecycle. |
| `GET/POST /api/apps/{applicationId}/environments` | List/create environments. |
| `GET/PATCH/DELETE /api/apps/{applicationId}/environments/{environmentId}` | Exactly one scoped environment. |
| `GET /api/environments/{environmentId}` | Environment runtime descriptor for operational clients. |

Application creation nests initial environment configuration. Runtime operations use environment IDs, never application-ID aliases or the dashboard default. Keep authorization, ownership checks and explicit environment selection. Existing standard protocols (Docker Registry and OTLP) and third-party provider API versions are outside this first-party contract. Persisted data migration preserves resources; it does not expose compatibility API routes.

## CLI and configuration

Keep `app` and `appId` in `minicloud.yml`. New configs identify the logical application. Configurations identify a canonical application ID. Fetch that application directly; reject missing or invalid IDs without attempting legacy aliases. Select deployment environments explicitly when multiple active persistent environments exist.

```sh
minicloud deploy --environment production
minicloud deploy --environment staging
minicloud environments list
minicloud environments create staging --plan "Staging small"
minicloud environments rename staging qa
```

These commands retain their current syntax. The application config is the shared target; do not require a separate app config per environment. Environment names resolve case-insensitively. With exactly one active environment, omission selects it. With multiple active environments, require `--environment <name>` and list choices; never select the dashboard default automatically. With none, return an actionable create-environment error. Deleting environments cannot be selected; a deletion-failed target remains blocked until lifecycle recovery.

Resolve identity, environment, authorization, and plan before build/upload/secret preparation or allocation. Existing plan precedence remains CLI override, YAML, then selected environment's saved binding. Selecting a plan does not move an existing server.

Console, logs, secrets, domain, storage, and other environment-sensitive CLI operations use the same resolution rule when supported; preserve existing explicit deployment/service IDs where those already identify one immutable target. Output names the selected app/environment. Preview deploy/destroy commands select their persistent source with `--environment` when multiple persistent environments exist. Never infer a source from root placement or the dashboard default.

## Preview environments

Represent existing branch apps as preview environments of the same application. Preserve branch names, URLs, resource identity, lifecycle, and current source-plan/secret-copy semantics. A preview is not a second application and does not require an Environment → Environment nesting layer. Its optional source environment records provenance only.

Do not normalize legacy branch names into persistent environment names blindly. If a branch and a persistent environment would collide under the new uniqueness rule, report the conflict and require an explicit migration mapping before cutover. Preserve the original branch reference separately from its mapped display name.

## Migration and rollout

1. **Inventory and preflight:** capture existing root/child relationships, environment/branch names, resource ownership, pending operations, provider bindings, domains, storage, and secret references. Identify hidden/internal app consumers such as database repository/server records; preserve their classification and exposure rules instead of turning them into customer apps automatically. Report orphaned parents, cross-organization links, duplicate names, and unsupported relationships; do not infer repairs.
2. **Additive backfill:** create one logical application per existing customer root and one explicit environment per root/child runtime record. Preserve existing environment names, including roots named staging. Null root names use the existing `production` fallback. Existing branch children become previews. Persist durable, unique legacy-runtime-ID → environment → application mappings.
3. **Preserve identity:** retain all existing runtime IDs, external server/volume IDs, generated hostnames, custom domains, deployment history, secret-store paths/versions, and resource ownership. New application IDs are distinct from runtime environment IDs. Renaming app/environment metadata does not regenerate routing identities or move infrastructure. Do not copy secret values merely to rename a scope.
4. **Lifecycle cutover:** use an explicit API/worker maintenance boundary: stop admission of new mutations, drain active jobs or verify a tested resume adapter, stop incompatible workers, run migration/backfill and reconciliation, then start compatible API/workers. Reads may be temporarily unavailable. Avoid unsupported mixed-version writers. Re-running backfill is idempotent. Reconcile counts and every ownership mapping before reopening writes.
5. **Canonical API:** expose only `/api` application/environment contracts. Remove old route and browser identity aliases. Unknown or environment IDs supplied as application IDs fail explicitly.
6. **Client rollout:** publish matching backend, worker, CLI and dashboard builds together. Mixed old/new clients are unsupported. Hosted workflow changes belong in `minicloud-services`; product implementation belongs here.

Independent legacy apps are not merged based on similar names. In particular, grouping MuniPages control/delivery/backups requires a separately reviewed explicit mapping and compatible placement behavior. This migration groups existing parent/child environments only. A future consolidation feature may be designed separately.

Before reopening writes, rollback may use a verified backup and previous binaries. After reopening writes, use a forward repair or a tested reverse migration covering newly created identities and resources; do not restore a stale snapshot over live changes. Retain migration bookkeeping only where required to preserve stored resources; it grants no API aliases.

## Scope boundaries

Include the explicit model, full resource ownership propagation, dashboard navigation, API ownership, CLI selection, preview mapping, deletion semantics, migration, documentation, and acceptance below.

Exclude deployment promotion, automatic environment cloning, cross-environment database copying, shared secrets, new RBAC, independent-app consolidation, new orchestration/scheduling capabilities, provider migration, resizing, and automatic production/staging provisioning. No implementation, commit, push, deployment, or provider mutation is authorized by this specification-writing task.

## Implementation sequence

1. Capture canonical API/CLI/lifecycle contracts in focused regression tests; inventory all runtime app-ID consumers and internal app records.
2. Add explicit entities, constraints, migration preflight/backfill, and PostgreSQL migration tests.
3. Update resource authorization, admission, workers, runtime/registry paths, locks, events, secret resolution, and deletion around environment ownership; use the single `/api` namespace.
4. Update dashboard list, scoped routes/header/selector, settings, creation/deletion, and empty/error states.
5. Update CLI identity resolution and documentation; use canonical application IDs and command syntax.
6. Qualify migration and canonical clients locally, then conduct separately authorized hosted rollout and live acceptance.

## Acceptance and evidence

| ID | Required result |
| --- | --- |
| A1 | One app with production and staging appears once in the Apps table, without Plan or Runtime columns; counts/search/filter results represent applications and identify matching environments. |
| A2 | App header and breadcrumb show app plus selected environment above services. Switching tabs/environments, refresh, deep links, browser back/forward, and organization changes preserve correct scope. |
| A3 | Slow previous-environment responses never replace current data; pending mutations retain their original target; console switches close the prior connection. |
| A4 | Different plans, database modes, service sets, secrets, domains, deployment results, and runtime observations remain independent. Missing/stale observations are not reported as healthy. |
| A5 | Application/environment rename preserves IDs, existing URLs, resources, secret references, and history; names are unique case-insensitively under concurrent writes. |
| A6 | Delete staging leaves production intact. Delete the initial/root-equivalent environment leaves its application and siblings intact through the canonical scoped API. Last-environment deletion leaves a valid empty app. |
| A7 | Application deletion covers all environments/previews, blocks new writes, preserves retained-volume policy, and exposes/retries failed cleanup without falsely reporting completion. |
| A8 | App creation atomically creates an initial environment; environment creation uses the plan catalog and starts without persistent-environment secrets or copied data. |
| A9 | CLI selects the sole active environment, requires a name for multiple, errors for none/unknown/deleting, and resolves before any build/upload/allocation. Dashboard default never changes CLI targeting. |
| A10 | Canonical application IDs work end to end. Old versioned product routes, runtime-ID application aliases, and legacy identity resolvers are absent; unsupported IDs fail without client fallback. |
| A11 | Populated PostgreSQL migration preserves runtime/provider IDs, domains/TLS bindings, storage, secret-store references, pending-operation handling, and deployment history. Re-run produces no duplicates; conflicts fail preflight. |
| A12 | Existing branches become previews under one app, preserving source semantics and URLs; naming conflicts require explicit mapping. Independently named apps remain separate. |
| A13 | Crafted cross-organization or mismatched app/environment/service IDs fail before side effects across API, console, uploads, registry/runtime registration, cancellation, and deletion. |
| A14 | Dashboard at 320, 375, and 390px remains usable, including keyboard environment selection, creation/error states, and scoped destructive-action dialogs; no horizontal primary navigation is required. |
| A15 | Compatible API/workers and old/new clients operate against the migrated database. Rollout/rollback gates are demonstrated on a representative synthetic fixture before hosted release. |

Verification must include focused .NET API/worker/CLI tests, real PostgreSQL migration/constraint tests, dashboard interaction tests and build, and browser acceptance of the scoped workflows. Follow `README.md` for checks. Source tests prove local behavior; hosted migration, live routing/provider identity, and customer browser flows require separate release evidence.

## Local implementation and release evidence

Implementation and validation reports are local execution output in `.local/artifacts/reports/`, not versioned specifications. Follow the [upgrade guide](../application-environment-upgrade.md) before a separately authorized rollout. Subsequent user instructions authorized local implementation and validation.
