# Apps and operations UX redesign

Status: Design approved; ready for implementation handoff.
Approval includes the final Apps-list corrections: no Plan or Runtime column.
Implementation and release acceptance remain separate.
Date: 2026-11-09 (America/New_York).
Owner of implementation: Muse sandbox. Owner of final validation: Codex.

## Outcome

Make Apps, Overview, Services, Domains and Console use one information hierarchy,
one collection pattern and the same status vocabulary. Preserve the restrained
Minicloud shell, monochrome actions, theme tokens, compact Card/table treatment,
shared toolbar, disclosures, overflow menus and centered dialogs established by
[DESIGN.md](../../DESIGN.md) and [Plan management UX](plan-management-ux.md).

A user should answer: which app needs attention, what is running, where it is
reachable, whether domain setup is complete, and where to inspect a failure.

This UX targets the refactored **App → Environment → Services** model in
[Application, environment, and service model](application-environment-model.md),
which is authoritative for identities, ownership, routes, migration and lifecycle.
The model and its `/api` contracts are prerequisites to qualify before dashboard
cutover. The drawings show the intended product and do not establish the current
completion status of that separate implementation. Do not approximate the
model by parsing app-name suffixes, grouping unrelated records, or polling every
legacy runtime from the Apps screen. Backend/model implementation and migration
qualification must precede dashboard cutover. This task produces design artifacts
only; it does not implement or authorize migration or product changes.

Applications own name, slug and environment collection. Each environment owns its
plan, database, services, domains, secrets, storage, deployments and runtime.
No app-level plan, database, runtime badge or latest-deployment badge represents
all environments. Existing independently registered control/delivery/backup apps
remain independent unless a separately reviewed explicit mapping consolidates
them; similar names are not evidence of common ownership.

The sidebar shows Plans because that is the current source design contract;
the attached production screenshots still show Providers.

## Drawings and authority

All content in these drawings is synthetic illustration, not a live state report.
The written contract controls fields, permission checks and behavior. Selector
menu, settings, lifecycle, preview and error-state behavior is specified below
even when not every state has a separate drawing. The drawings
control hierarchy, spacing and density. They deliberately exclude browser chrome.

| Drawing | Design decision |
| --- | --- |
| [01 Apps](assets/apps-ux/01-apps.png) | One application row with compact environment summaries; per-environment deployment and deletion feedback; no Runtime or Plan columns. |
| [02 Overview](assets/apps-ux/02-overview.png) | Operational summary and service entry points, rather than a duplicate URL inventory. |
| [03 Services](assets/apps-ux/03-services.png) | One row per service; primary address; details disclosed on demand. |
| [04 Domains](assets/apps-ux/04-domains.png) | Separate ownership from routing and HTTPS; compact hostname rows. |
| [05 Console](assets/apps-ux/05-console.png) | Runtime logs above the fold; agent and container details are secondary. |
| [06 Narrow viewport](assets/apps-ux/06-mobile.png) | Visible app/environment context, drawer navigation, stacked summary and compact operational navigation menu. |
| [07 Environment selector](assets/apps-ux/07-environment-selector.png) | Environment choice above all operational content, with explicit creation and management links. |

Editable illustration source: [source/](assets/apps-ux/source/). These are static
mockups using existing dashboard Button/Card/Badge components. Links and controls
illustrate intent; they do not invoke production APIs. Do not ship this illustration
source as application code.

## Evidence reviewed

Paths below are relative to the public product checkout.

- `modules/dashboard/app/core/components/PageScaffold.tsx`: title, breadcrumb and spacing.
- `modules/dashboard/app/core/components/CollectionToolbar.tsx`: collection actions,
  search, filter/sort popover, disabled refresh and responsive wrapping.
- `modules/dashboard/app/modules/Apps/AppsModule.tsx` and `components/AppCard.tsx`:
  current Cards/List presentation, app/deployment fields and deletion state.
- `modules/dashboard/app/modules/Apps/AppDetailModule.tsx`: detail tabs, overview,
  service disclosure/actions, latest deployment and environment/branch controls.
- `modules/dashboard/app/modules/Apps/useAppServiceInventory.ts` and
  `serviceRuntime.ts`: 15-second polling, retained observations and 120-second freshness.
- `modules/dashboard/app/modules/Apps/components/AppDomains.tsx`,
  `ManageServiceDomainsDialog.tsx`, `CustomDomainSetupDialog.tsx` and
  `DomainDnsRecord.tsx`: ownership, hostname bindings, DNS guidance and removal guards.
- `modules/dashboard/app/modules/Apps/AppConsolePanel.tsx`, `AppTelemetryPanel.tsx`,
  `api/runtimeApi.ts` and `models/controlPlane.ts`: supported data and logging semantics.
- `modules/dashboard/app/styles.css`: existing light/dark and status tokens.
- [Application/environment model](application-environment-model.md): target ownership,
  `/api` summaries, scoped routes, selector, settings, previews and lifecycle.
- User screenshots: evidence of layout and usability problems, not instructions
  or proof of current backend health.

## Shared contract

### Shell and detail navigation

Use PageScaffold and the current sidebar. Content uses the shell's existing width;
remove oversized inner margins, not the shell itself. Use a 24px title, 14px body,
12px supporting text, 16px icons, 8px control gaps, 16–24px section gaps and existing
radii. Typical table rows are 56–72px; long names may grow naturally. Do not set
fixed heights that clip text. No decorative counters or new component dependency.

Breadcrumbs are navigation, not another oversized title. The operational header
reads `Apps / MuniPages / production`, then `MuniPages [production ▾]` above the
navigation and service content. Show the app slug as supporting app identity;
label plan/provider/database metadata as belonging to the selected environment.
No environment suffix is baked into the app display name. Header actions are
Refresh and an explicitly scoped settings/More menu. Application settings and
Environment settings are separate destinations, never one ambiguous Delete action.

The prominent selector lists persistent environments first and previews with kind
and provenance where relevant, current selection, observed runtime/freshness, and
permitted Add environment / Manage environments actions. Production and staging
are peer names, not privileged kinds. Search or scroll a large menu within its
bounded viewport. Keyboard users can open it, move between choices, select and
return focus. Do not place environment management below the operational content.

Canonical navigation follows the domain specification:

- `/apps` shows applications once.
- `/apps/{applicationId}` opens the dashboard default environment or the
  create-environment empty state when none exist.
- `/apps/{applicationId}/settings` contains application metadata and environment management.
- `/apps/{applicationId}/environments/{environmentId}/{tab}` identifies an
  immutable application/environment pair for each operational screen.

Operational labels are Overview, Services, Storage, Domains, Secrets, Deployments,
Console and Logs, in that order. Logs means explicit application telemetry;
Console remains runtime/container output. Use the existing Tabs primitive with a
light underline, selected state and a separate visible keyboard focus ring.
On narrow screens replace the row with a compact labeled section menu or wrapping
layout: primary navigation must not require horizontal scrolling.

Switching environment preserves a supported tab but replaces all scoped data,
queries, filters, subscriptions and diagnostics. Cancel/discard prior-environment
requests so late responses cannot render under the new header. Clear or scope
cached data by organization, application and environment; do not display retained
production rows beneath a staging heading while staging loads. Preserve correct
scope through deep links, reload and browser back/forward. Every operational read
and write resolves the selected immutable environment explicitly.

Mutating dialogs, deployment progress, console connections and confirmations name
both app and environment. A submitted operation remains bound to its original
immutable target; switching never retargets it. Close prior console connections
on switch. Unsubmitted forms close or use the established unsaved-input handling;
never carry an unsubmitted production mutation into staging. Only canonical application/environment links are supported; do not resolve legacy runtime-ID links.

### Collections and actions

Apps, Services and hostname collections use the same Card/table structure. Reuse
CollectionToolbar and collection query utilities; no replacement global toolbar.
Refresh, Search, Filter & sort stay in their established order. One primary action
and, when useful, one outlined secondary action appear on the right. Count and
active-filter information sit immediately above the table. Do not put another
search/refresh toolbar inside every card. Retain useful data while refreshing.

Explicit links navigate; disclosure buttons expand; overflow menus hold secondary
and destructive actions. Plain table rows are not undisclosed click targets.
Icon-only copy/open/more/refresh buttons have entity-specific accessible names,
tooltips and visible focus. Copy gives a brief confirmation and copies the full
scheme-bearing URL, regardless of how it is displayed. Open uses a new tab with
appropriate link safety attributes. Buttons have at least 44px touch targets on
coarse pointers while desktop rows remain compact.

No grid/list toggle for the redesigned Apps collection: a table is the default and
only presentation in this scope. Retire only its view preference; keep existing
search, filters and sort preferences. Do not change view choices in other modules.

### State vocabulary and data integrity

| Fact | Visible treatment |
| --- | --- |
| Latest deployment | Label it “Latest deployment”; preserve actual Succeeded, Failed, Deploying, etc. Never call this app health. |
| Container runtime | Preserve Running, Healthy, Stopped, Unhealthy and other actual states. Running does not imply a passing health check. |
| Observation unavailable | Unknown / No observation; neutral, never red or green. |
| Observation stale | Last known state plus Stale and relative observation age; never present it as current. |
| Observation refresh failed | Keep last observation and show “Could not refresh” with Retry; freshness continues to age. |
| Agent status | Online/offline or unknown from the existing runtime contract; distinct from service state. |
| Domain ownership | Verified, Pending verification or actual failure; distinct from route and certificate. |
| Hostname routing / HTTPS | Separate domain/apply/DNS status from HTTPS status. Preserve actionable failures. |
| Deletion lifecycle | Deleting / Deletion failed identifies the affected environment or whole app, with a scoped review/retry action. |

Use existing semantic status tokens in both themes. Every status has text; color
alone carries no meaning. Neutral states are neither failures nor success.
Reuse existing freshness thresholds: 120 seconds and current clock-tolerance rules.
Do not reimplement them in individual screens. Aggregate runtime must use the same
service set and fresh observations as Services, exclude unknown/stale members from
any “running now” success claim, and expose partial/unavailable counts. Do not add
health checks, uptime percentages, CPU, memory, cost, quota or invented metrics.

Use relative times for scanning, with exact local date/time and timezone available
in accessible supporting detail. Missing completion times show “In progress” only
for an actual active deployment; otherwise show a neutral missing-time label.
Never substitute the current time for missing data.

### Addresses and disclosure

Collapsed rows show one preferred enabled public address: existing primary binding
first, then an enabled custom hostname, then enabled platform hostname, with stable
ordering for ties. Do not change the stored primary binding as a side effect.
Show `+N hostnames` when more exist. Do not wrap generated platform hostnames across
several lines in the collapsed row. Display the hostname without `https://` only
when the actual protocol is retained in the link and accessible name.

Details expose complete URLs, kind, routing/HTTPS status and disabled bindings.
Full hostnames must be selectable and readable with wrapping; a tooltip alone is
insufficient. Private services say Internal only; copy internal DNS is offered
only when supplied by the current contract. Never invent a public URL.

## 01 — Apps collection

Question answered: which app do I need to open?

Show one application row/group, with exact app name link, slug, environment count
and compact environment subrows. Align Environment, Latest deployment and Actions
across those subrows. Do not display Runtime or Plan columns in the Apps list.
Runtime and plans belong to the selected environment’s details and settings.
The domain model specification reflects this approved screen decision.
Persistent environments appear first; previews may be
collapsed behind their count. Do not collapse a preview that matches a filter or
requires attention without making that condition visible. App counts count
applications, regardless of how many environment subrows appear.

The target `GET /api/apps` provides application/environment summaries. Display
per-environment deployment result/completion age only when supplied.
No deployment means No deployments yet. Do not fetch or display runtime observations
on this screen, derive health from deployment success, or introduce N+1 runtime
polling. Runtime freshness and missing-observation states belong inside the
selected environment.
If the dependency is unavailable, expose that implementation gap rather than
shipping the legacy duplicate-app list as the completed design.

Search covers app name/slug, environment names and associated domains, returning
each application once. Environment filters (name/kind/deployment
where supported by the new summary) require at least one environment to satisfy
all selected environment predicates together. Mark the matching environment;
never combine predicates from different environments to produce a match.
Remove the main/branch app filter; previews use environment kind. Default to app
name ascending with stable ties, then persistent before preview environments.

Environment names are explicit links into their scoped Overview; the app name
opens the dashboard default. A zero-environment application remains visible with
No environments and the permitted Add environment action. Environment deletion
failures stay under the affected environment with Review & retry; whole-app
deletion has separate feedback at app level. Do not scatter errors into unrelated
columns. Keep deleting targets visibly pending and block unsupported new work.
Creation remains Create app; Deployments remains the outlined secondary action.

### Apps column and action contract

| Column | Required content | Interaction |
| --- | --- | --- |
| App | Exact name, slug, total environment count; rendered once per application group. | Name opens the dashboard default; zero environments opens the explicit empty state. |
| Environment | Exact name and Persistent/Preview classification. | Name opens that immutable environment’s Overview. |
| Latest deployment | Environment-specific result and completion age, or active-operation start age. | Explicit deployment link opens its detail; no deployment shows No deployments yet. |
| Actions | Environment-scoped overflow on each environment subrow. | Menu labels identify the environment; application settings remain a separately labeled app action. |

Do not request runtime status for this collection or expose runtime/plan filters.
An application with multiple environments has one app identity cell spanning its
subrows, with equivalent semantic grouping in responsive layouts. Preview
visibility does not change the total environment count. Preview disclosure is
an explicit button with its expanded state; preserve context when it opens.
A matched environment remains visible even if previews normally collapse.

## 02 — Overview

Question answered: what is the current situation and where do I go next?

Use three compact summary sections for the selected environment: Runtime, Latest
deployment, Domains. They present facts already loaded for that immutable scope;
independent read failures remain local. Runtime includes fresh running/observed counts and observation age. Domain
counts label bindings as hostnames rather than confusing them with registered root
domains. If an accurate count cannot be derived, show unavailable instead.

A compact services table shows Service, observed Runtime, preferred address and a
Details link. One row per service, including private services. It does not repeat
every URL or per-row observation timestamps when one shared snapshot applies.
Details navigates to Services with that service expanded and brought into view.
View all services and View deployment navigate explicitly within the selected
environment. Move environment management out of the legacy below-content panel
and into the header selector/application settings. Preview provenance remains
available in environment settings; previews do not become nested applications.

Errors and degradation appear before the service table when actionable; avoid a
permanent green “everything healthy” banner. A stale runtime and successful last
deployment may coexist visibly.

## 03 — Services

Question answered: what services exist, how are they running and how do I reach them?

Columns: Service disclosure/name, Access, Runtime, Primary address, Actions. Use
one shared observation timestamp where valid. Retain search/filter/sort using
existing supported data. Private and unknown services remain visible by default.

Disclosure expands an inline full-width detail row directly below its service.
The drawing's detached example is explanatory only. Expanded content contains:
all addresses with copy/open controls and routing/HTTPS status; image, port,
routing path, health-check path and storage mounts when available; runtime
observation, health and restart details; existing internal-DNS, domains, secrets
and application-log actions as permitted. Do not hide actionable configuration.
Do not put secret values or credentials into the drawing or ordinary rows.

“View application logs” opens Logs with both the environment and service filter set. This
requires a dashboard navigation-state change: switching the tab alone is not
sufficient. Keep runtime-output navigation explicitly labeled if offered. Do not
add restart, stop, shell or redeploy controls; those APIs are outside this scope.

## 04 — Domains

Question answered: is ownership verified and is each address ready for traffic?

All ownership registrations and hostname bindings belong to the selected
environment. First collection: owned domains, with Domain, Ownership, Actions. Add domain opens
the existing centered setup dialog. Ownership settings is explicit; Remove lives
in overflow with the current guarded confirmation and in-use restriction.

Second collection: service hostname bindings, with Hostname, Service, Routing,
HTTPS, Actions. Show custom bindings by default. A secondary “Platform hostnames
(N)” disclosure exposes automatic addresses in the same row format; automatically
open it when any platform address requires attention. Counts include their actual
scope. Unresolved ownership warnings stay on the relevant root domain.

Add hostname opens a centered dialog using existing binding operations, choosing a
public service and eligible verified domain; no disabled orphan “Use domain” CTA.
If none exists, explain the prerequisite and provide Add domain. Editing reuses
ManageServiceDomainsDialog; do not invent deletion or editing of automatic
hostnames where the API prohibits it. Existing primary designation is retained.

DNS guidance shows record type, name, expected value/target IP and copy controls
using DomainDnsRecord; preserve existing verification/recheck actions and errors.
Do not say HTTPS ready until SSL status confirms it. Ownership removal with bound
hostnames explains the exact prerequisite; it never silently removes routes.

## 05 — Console

Question answered: what runtime output explains this problem?

Keep the Console tab name and label the main content Runtime logs. State plainly that it is runtime output, not a command terminal. Top strip:
agent status, last seen, runtime version and proxy status. Missing fields remain
unknown. Runtime details opens an existing-style disclosure, not a new inspector.

Place runtime log controls and the log viewport immediately below that strip.
Use the shared toolbar with searchSupported=false and sorts=[]; retain the
existing Filter & sort label for component consistency. Its content explains
server-provided order and exposes only source, service, since and tail (1–1000,
default 200; default since 30m). The drawing uses the same Filter & sort label.

Use a neutral log surface, monospace output, aligned time/source/message tracks,
line wrapping or an explicit horizontal-scroll option, selectable text and a
visible loaded-record count. Display “Server order” and keep the order returned
by the API. No search, client reordering, terminal prompt, invented streaming/pause control or auto-refresh is added.

The log viewport starts within the first 450px of detail content at 1440×900 and
receives at least 280px height before optional container inventory. Long output
scrolls inside this clearly bounded region. A compact Runtime services table below
or collapsed section contains service/container name, observed state and restart
count, with full technical identifiers in disclosure. Diagnostics appear near the
log controls when present, including existing deployment links and errors; do not
bury failures after an enormous inventory.

stderr labels the stream; it does not imply severity Error. The synthetic output
is presentation content, not a schema example. Logs stays separate:
OTLP ingestion, telemetry setup/credentials, retention and its supported severity,
text, trace, service, time and auto-refresh controls remain intact.

## 07 — Environment selection, settings and lifecycle

Question answered: which environment am I operating, and what does this action affect?

Application settings contain name/slug presentation, the environment collection,
dashboard-default selection and Delete app. Environment settings contain rename,
plan selection under existing placement restrictions, database metadata,
preview/source information, dashboard-default action and Delete environment.
Settings links name their scope. Do not add a new RBAC layer: existing organization
roles remain authoritative, and hidden controls never replace server checks.

Create app asks for app name/slug plus initial environment name (default
`production`), plan and supported database selection; backend creation is atomic
and does not itself provision infrastructure. Add environment asks for name, plan
and supported database selection using the organization plan catalog. Explain
empty, unavailable and disabled-plan states. Persistent environments start with
empty secrets and no copied data; do not offer implicit cloning or promotion.
Previews retain the domain model's explicit legacy provenance/secret-copy behavior.

Environment names are unique case-insensitively within an app; preserve display
casing and validation from the domain contract. Metadata rename does not change
IDs, generated URLs, provider resources, secret references or history. Production
has no structural privilege; staging can be created first or selected as default.
The initial dashboard default is the first created environment. Deleting the
default chooses the oldest remaining persistent environment, then oldest preview,
with ID as deterministic tie-breaker, or null when none remain. This dashboard
choice does not change CLI environment-selection rules.

Delete environment identifies `MuniPages / staging` and affects only that target.
Deleting the initial/root-equivalent environment leaves its app and siblings.
Deleting the last environment leaves a valid empty application with Add environment;
no deployment is possible until an environment exists. Delete app lists every
affected environment, including previews, and completes only after required child
cleanup. Retained-volume/billable-resource policy remains visible and unchanged.
App deletion blocks new environments and deployments; failed cleanup remains
visible with a correctly scoped retry. Do not silently target the dashboard default
for unsupported legacy root-ID operations; no API compatibility adapter is supported.

## Responsive and accessibility

At 1440px: 216–240px existing sidebar, compact header, full-width working area.
At 1024px: use the current shell collapse behavior, wrap toolbars, keep actions
visible. At 320–390px: drawer navigation, visible app/environment identity and
selector, stacked summary, compact section menu, full-width dialogs, touch targets
and labeled fields. Primary navigation never requires horizontal scrolling.
Table content may scroll within a labeled region; the page itself must not scroll horizontally.
Keep App/Service identity and explicit actions reachable. For long collections,
prefer the existing responsive labeled-cell pattern; preserve all fields.

Target WCAG 2.2 AA. Use real table headers, keyboard disclosure buttons with
aria-expanded/aria-controls, tab semantics and visible focus. Dialogs trap focus,
Escape closes when safe and focus returns to the trigger. Error summaries identify
fields; failed saves preserve input. Announce pending/error/copy states without
re-announcing the entire table on polls. Preserve contrast in dark mode, reduced
motion and organization-scoped state isolation.

## Required states

Every collection: initial skeleton at final row geometry; populated; legitimate
empty state with relevant create/setup action; no matches with Clear filters;
read failure with Retry; refresh in progress retaining rows. Never treat a failed
request as empty success. No apps, an app with no environments, an environment
with no services and an environment with no deployments are different states.
Selector loading/failure, renamed/deleted deep-link targets, no matching
environments, preview collections and partial summary failures need explicit
feedback without changing the target silently.

Every operational panel: unknown, stale, partially observed, disconnected,
refresh-failed-with-last-data and actual current runtime failure. Domain dialogs:
unverified, DNS pending, routing pending, certificate pending/failure, duplicate,
in-use removal and permission denied. Console: fetching, no records matching
filters, runtime unavailable, diagnostic failures, invalid filters and log fetch
failure distinct from an empty result. Permission-gated actions follow current
capabilities; frontend presentation never substitutes for server authorization.

## Muse implementation handoff

1. Read this spec, DESIGN.md and folder instructions; check branch/status and
   preserve concurrent work. Do not push or deploy under this handoff.
2. Read the authoritative application/environment model and verify its explicit
   `/api` identities, summaries, scoped operations and migration qualification are
   available. If they are not, report the dependency and work against agreed
   synthetic contracts for design review; do not fake grouping from legacy names.
   Backend/migration work is a separate implementation lane and is not performed
   by this specification-writing task.
3. Implement the dashboard in public `minicloud/modules/dashboard` source after
   that prerequisite. Reuse API helpers, runtime freshness helpers and current
   organization permission/mutation guards. Apply scoped routes, selector,
   settings and creation/deletion behavior as well as the five operational views.
   Do not modify hosted service workflows or independently consolidate apps.
4. Preserve resource identities, explicit environment routing identity and preview semantics
   through the domain contract. No new provider behavior, dependency, shared
   resources, cloning, promotion or placement movement belongs in this UX work.
5. Supply implementation summary, changed-file list, checks, screenshots at
   1440×900, 1024×768, 390×844 and checks at 320/375px in light and dark themes,
   plus remaining gaps. Return control for Codex validation; implementation
   self-report is not final acceptance.

This approved spec and its drawings are the implementation handoff artifacts.
Local implementation work packages and execution evidence (`.local/artifacts/reports/apps-operations-ux-implementation.md`, git-ignored)
defines the delivery sequence and acceptance record. Muse implements separately;
Codex validates the completed result. Writing these specs does not start an agent
session, authorize a push or authorize production rollout.

## Final validation gate (Codex)

- Apps shows each logical application once, with production/staging as peer
  environment summaries. No Runtime or Plan columns appear in the Apps list,
  and no app-level deployment/health claim appears. Test
  app counts, preview disclosure, zero environments and same-environment filter
  matching. Independently registered control/delivery/backups apps stay separate.
- Verify canonical application/environment routes, default selection, deep links,
  browser back/forward with canonical IDs. Switching environment discards late
  responses, closes prior console connections and never retargets pending writes.
- Test scoped create/rename/default/delete flows, initial environment atomicity,
  zero-environment apps, deletion failure retries and whole-app cleanup guards;
  reject crafted application/environment/service ownership mismatches.
- Successful deployment + stale/stopped runtime is visibly distinguishable on
  Overview and Services. Test absent observation, partial inventory and refresh failure.
- Overview counts match the same service/domain data used by their detail views.
  No multi-address wall appears before the primary tasks.
- Service disclosure is keyboard usable; complete URLs, disabled bindings, image,
  routing, storage and internal DNS remain accessible. Application-log navigation
  actually sets the selected service; no invented lifecycle actions appear.
- Domains keeps ownership, routing and TLS separate. Test pending DNS, SSL failure,
  no eligible domain, private services, read-only platform binding and guarded removal.
- At 1440×900 Console shows actual output before container inventory. Only supported
  filters are sent; source/service identity and stream are accurate. Test empty
  results, unavailable runtime and independent diagnostics failure.
- Test long app/service/hostnames, no records, no matches, failed save, read-only
  roles and organization switching; no previous-organization data flashes.
- Keyboard, focus restoration, copy feedback, dark contrast, touch controls and
  320/375/390px layout and non-scrolling primary navigation meet the shared
  contract. Inspect screenshots, not builds alone.
- Run affected dashboard tests, then `npm --prefix modules/dashboard test` and
  `npm --prefix modules/dashboard run build`. Record any unavailable checks.
  The repository has no separate lint script; do not claim it ran. Backend/model
  migration acceptance additionally requires the domain specification's .NET,
  PostgreSQL and canonical-client evidence before dashboard release;
  mockups, dashboard tests or a build do not prove those prerequisites.
- Report acceptance or concrete discrepancies against this contract and drawings.
  No production deployment or push is implied by acceptance.
