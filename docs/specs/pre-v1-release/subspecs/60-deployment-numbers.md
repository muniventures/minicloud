# T60–T62 — Sequential deployment numbers

Status: not started. Repository: `muniventures/minicloud`.
Read the [feature overview](../1.pre-v1-release.md) and [execution plan](../0.execution-plan.md). Each task below is a separate Copilot issue/PR; the user triggers the sessions. This document authorizes specifications only, not implementation or dispatch by Codex.

## Order and ownership

| Task | Deliverable | Hard dependencies | Can run in parallel with |
| --- | --- | --- | --- |
| T60 | Persistence, backfill, allocation and API/shared contracts | T00, T30 backend and T50 SSH backend merged | Independent installation/release tasks outside its files |
| T61 | Dashboard number-based deployment identity | T60, T51 SSH dashboard and T42 setup merged; route-file handoff | T62 |
| T62 | CLI number-based deployment identity | T60 and T52 SSH CLI merged | T61 |

T30 → T50 → T60 serializes shared model, DbContext and migration-snapshot writers. T51/T52 hand off shared app/environment components and CLI command files before T61/T62. Read-only investigation may overlap; concurrent writes to those files may not.

## Fixed identity contract

- Persist C# `Deployment.Number`, JSON `number`, in exact invariant format `yyyyMMdd.N`, for example `20261110.1`. Use one server `TimeProvider` UTC instant for its date and `CreatedAt`.
- `N` is an unpadded positive integer starting at 1 for each organization/UTC date. Enforce uniqueness on `(organization_id, number)` and non-nullability after backfill. Different organizations may have the same number.
- Every persisted deployment receives a number, including waiting preparations and asset jobs. Idempotent preparation replay returns its existing number. Failure, cancellation, redispatch and deletion never change or reuse a persisted number.
- Keep opaque deployment IDs as primary keys and for foreign keys, queue/lease ownership, runtime payloads, telemetry, artifacts, remote directories and image/container naming. Preparation heartbeat/submission and machine secret-resolution identifiers remain internal operation contracts.
- Customer detail/action resolution requires both organization and number before loading children or admitting mutations. No global lookup followed by authorization, opaque-ID fallback, legacy route alias, organization timezone feature or unrelated UX redesign.

## T60 — Persistence, backfill and API

Own the deployment model, daily counter, DbContext/new migration; lifecycle/preparation allocation; deployment contracts/mapping/summary projection; deployment controllers, console URL provider and corresponding backend tests. No dashboard components or CLI commands. Start from current `main` after the declared shared-file handoffs.

Existing entry points include `Deployment`, `DeploymentLifecycleService`, `DeploymentPreparationService`, `AssetReleaseService`, `AppSummaryReader`, `DtoMapping`, `DeploymentsController`, `DeploymentPreparationsController`, `DeploymentDtos` and `AppDtos`. Inspect all creation and projection paths, not only the ordinary deployment POST.

### Frozen T00 wire contract

`DeploymentCreateResponse`, `DeploymentResponse`, `LatestDeploymentResponse` and `DeploymentCancellationResponse` gain required JSON `number` (C# `Number`); retain every existing field and opaque `id`. Preparation creation/replay returns the same `DeploymentCreateResponse` shape. `AppSummaryReader` must populate number before `DtoMapping` runs, including application/environment summaries and list/latest results; no empty/default number on detached projections. If a customer-facing asset response links a deployment, retain opaque `deploymentId` for correlation and add nullable `deploymentNumber` for the customer link. Do not rename runtime payload fields.

| Canonical customer route | Required scope |
| --- | --- |
| `GET /api/deployments/{deploymentNumber}?organizationId=...` | Existing `ReadDeployments` authorization |
| `POST /api/deployments/{deploymentNumber}/dispatch?organizationId=...` | Existing dispatch permission/lifecycle guards |
| `POST /api/deployments/{deploymentNumber}/cancel?organizationId=...` | Existing cancel permission/lifecycle guards |
| `POST /api/deployments/{deploymentNumber}/refresh?organizationId=...` | Existing refresh permission/runtime guards |
| `GET /api/deployments/{deploymentNumber}/events?organizationId=...` | Existing event read permission |
| `GET /api/deployments/{deploymentNumber}/activities?organizationId=...` | Existing activity read permission |
| `GET /api/deployments/{deploymentNumber}/logs?organizationId=...` | Existing log read permission; preserve other log query filters |

Creation/list/secret validation remain `POST`/`GET /api/deployments` and `POST /api/deployments/validate-secrets` with existing request/filter fields. Internal `POST /api/deployments/{deploymentId}/resolve-secrets` and `POST /api/deployments/preparations/{id}` remain opaque-ID protocols with their existing independent authentication/authorization. Artifact/image-publication/asset protocols and `preparationId` stay opaque. No number-to-ID client fallback or alias is permitted.

Parse at most 28 ASCII characters: exactly eight date digits, `.`, and 1–19 unpadded decimal digits. Validate a real `yyyyMMdd` Gregorian date with invariant culture and `N` in `1..Int64.MaxValue`; whitespace, signs, Unicode digits, leading-zero counters and opaque IDs are invalid. Counter overflow fails explicitly (`deployment_number_exhausted`, 409) without inserting. Use 404 `deployment_not_found` with the existing error envelope for missing/malformed/not-found numbers in an authorized organization; missing organization input is 400 with the existing validation envelope, and inaccessible organization/action retains existing 403/resource-hiding behavior. Resolve only by `(organizationId, number)` after organization authorization, never load a global matching deployment first.

Creation/preparation `Location` and `statusUrl` are `/api/deployments/<number>?organizationId=<escaped-id>`; console links are `<configured-console-origin>/deployments/<number>?organizationId=<escaped-id>`. T61 validates any URL organization hint against loaded memberships and selects it before fetching; without a hint use the selected authorized organization. Never infer organization from the number. Preserve the hint and other safe query parameters through login. Links and pending requests are keyed by both organization and number.

Allocate with one UTC instant for date/`CreatedAt`, within the same transaction as the deployment insertion. Reserve migration `20261110120000_DeploymentNumbers` after T50's merged snapshot; non-null `number`, unique `(organization_id, number)` and a separate counter keyed by `(organization_id, utc_date)` are the persistent contract. Counters survive deployment deletion; no backwards recomputation from remaining rows. Existing preparation submission must reuse both its persisted number and original `CreatedAt`, even if the build crosses UTC midnight.

### Implementation requirements

1. Add a durable daily counter keyed by organization/date. Atomically allocate and insert inside the same EF execution-strategy-wrapped transaction, with provider-correct concurrency handling. Never use an unlocked row count or maximum. A rolled-back insert must roll back its counter increment.
2. Add a forward migration; do not rewrite committed migrations. Partition historical deployments by organization and UTC creation date, order by `created_at` then opaque `id`, assign row numbers and seed counters to each partition's maximum. Make number non-null and add the unique index after backfill. Preserve equivalent PostgreSQL/SQLite behavior and every existing relationship.
3. Cover normal creation, persisted local-build preparations and asset jobs. Preserve preparation idempotency and existing allocation/ownership locks.
4. Include both `id` and `number` in create/detail/list/latest-deployment responses, detached summaries and preparation creation responses. In particular, populate number in `AppSummaryReader`'s projected deployment and every DTO mapping path.
5. Customer `GET /api/deployments/{deploymentNumber}`, POST actions `dispatch`, `cancel`, `refresh`, and GET `events`, `activities`, `logs` require `?organizationId=...`. Centralize bounded, culture-invariant parsing and composite resolution. Use the existing `deployment_not_found` shape for missing/malformed/not-found numbers; retain existing authorization/resource-hiding responses.
6. Authorize the supplied organization and existing action permission. API keys cannot select a different organization. No response may reveal another tenant's matching number. Preserve all lifecycle guards and independent machine authentication, including opaque-ID `resolve-secrets` and preparation protocols.
7. Return number-based `Location`, `statusUrl` and console URLs. Retain opaque response `id` for internal correlation, not the normal customer label. Returned links carry the organization scope needed to follow them directly.

Focused checks: deterministic empty/populated backfill, timestamp ties, UTC rollover, counter seeding, concurrent creates, rollback, same number across organizations, preparation replay, immutable redispatch/cancel numbers, every customer endpoint's permission/isolation and unchanged worker/runtime opaque identities.

## T61 — Dashboard

Own deployment API helpers, typed models, deployment route helpers, list/detail views, app-environment deployment history and corresponding tests. No backend/schema changes. T51 must finish its app/environment components; obtain the auth/setup owners' handoff before editing shared `dashboardRoutes.ts` or `app/routes.tsx`.

- Use `deploymentNumber` route parameters and number-based links. Global detail becomes `/deployments/20261110.1`. Preserve canonical `/apps/:applicationId/environments/:environmentId/:tab` navigation; do not recreate the historical `/apps/:appId/deployments/...` route.
- Pass selected organization ID on every detail/action/event/activity/log request, including cancel. Direct links resolve only in the selected authorized organization; do not search globally or infer a tenant from a number.
- Display/search/copy numbers in headings, breadcrumbs, history, metadata, accessible labels, refresh controls and console links. Keep opaque IDs only for internal keys/state where necessary.
- Cancel or invalidate stale requests on organization/environment changes, even when both organizations contain the same number. Never let a stale response or pending action target the newly selected organization.

Focused checks: same-number tenant switching, stale responses, deep links, list/history search, labels/copy, dispatch/cancel/refresh and read-only permissions. Run affected dashboard tests, typecheck and build; do not redesign other Apps/Plans UX.

## T62 — CLI

Own deployment API calls/models, deploy/monitoring commands, renderers, polling/local-build-session callers and corresponding tests. T52 hands off shared command files first. No backend/schema, dashboard or CLI distribution-policy edits.

- Use returned numbers for creation output, polling, status, cancellation, deployment-specific logs and console links. Resolve authenticated/selected organization before every number-based request.
- Replace the `dep_` heuristic with bounded, culture-invariant deployment-number parsing. Preserve app-name disambiguation. With no explicit deployment, select the latest within the current organization and continue using its number.
- Preserve opaque preparation/artifact identifiers in internal protocols; expose opaque deployment IDs only in explicit diagnostic correlation, not ordinary output.

Focused checks: create/poll/cancel, status/logs, malformed input, app-name disambiguation, omitted arguments and identical numbers in two organizations. Run affected CLI tests/build against T60's merged contracts and T20's source-build delivery path.

## Copilot assignments and handoff

**T60:** Implement only T60 after its prerequisites merge. Preserve internal IDs and canonical organization/environment ownership. Submit one PR with focused allocation/migration/API regressions, commands/results and concrete gaps.

**T61:** Implement only T61 after T60/T51/T42 and shared-route handoff. Consume merged contracts, retain canonical navigation and stale-response guards, and report focused dashboard results. No backend or unrelated UX rewrite.

**T62:** Implement only T62 after T60/T52. Preserve internal preparation IDs, consume number/organization contracts, and report focused CLI results. No new distribution or compatibility path.

No agent may deploy, provision resources, access hosted credentials or claim final acceptance. After all required implementation PRs merge, Codex performs combined migration, concurrency, canonical API/dashboard/CLI and cross-organization integration tests. Rollout applies migration/API before number-only clients; never roll the API back beneath those clients. CI or an individual agent's completion report is not final qualification.
