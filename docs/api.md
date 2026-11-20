# Minicloud API

Minicloud uses one unversioned first-party namespace: `/api`.
The prelaunch application/environment cutover deliberately removes old product
API routes and identity aliases. Clients and server must be updated together.

- `/api/apps` lists/creates applications with environment summaries.
- `/api/apps/{applicationId}` reads/updates/deletes an application.
- `/api/apps/{applicationId}/environments` lists/creates its environments.
- `/api/apps/{applicationId}/environments/{environmentId}` reads/updates/deletes
  one environment, with ownership and lifecycle checks.
- `/api/environments/{environmentId}` returns the runtime descriptor used by
  operational clients. Its telemetry, logs, storage and image actions use the
  same explicit environment identity. Plan and preview actions also target this
  environment resource; they do not resolve application aliases.
- Other product resources use `/api`, including providers, plans, deployments,
  secrets, runtime ingestion, registry credentials and CLI authentication.

Anonymous `GET /api/instance` returns `product`, `protocolVersion` and `appDomain`.
Packaged servers additionally report `serverVersion`, `sourceRevision` and
`cliCompatibility` from verified release metadata. Compatibility includes the
protocol, optional half-open version interval and explicit source revisions;
it does not imply that CLI and server versions match. Source builds without
release metadata leave these additional fields null. See the
[server release contract](../packaging/server/README.md).

Application creation accepts nested `initialEnvironment` configuration. Unknown
flat creation fields are rejected. Configuration `appId` is a canonical
application ID. Deployments require environment selection when multiple active
persistent environments exist. No client retries against an older API, resolves
runtime IDs as application aliases, or implicitly chooses the dashboard default
for CLI deployment.

Every request retains organization authorization and resource ownership checks.
Browser users authenticate through instance-local Kratos and opaque API sessions;
existing users require explicit host-side identity binding, never email adoption.
Summary reads
select only the latest non-asset deployment and active domains; no full history
graph or response cache is needed.

Docker Registry, OTLP, OpenBao, Loki and provider version identifiers belong to
those external protocols and remain unchanged. They are not parallel Minicloud
product APIs. Stored migration metadata preserves existing resources without
publishing identity-resolution aliases.

## Runtime log ingestion

`POST /api/runtime/ingest/logs` scopes records to the runtime server identified by
its bearer token. A log is identified by its source, cursor and observed
timestamp within that server. Replaying a retained log or sending it more than
once in a batch does not write another row. `accepted` counts newly written
rows, `rejected` counts records from sources not allowed for the deployment, and
`lastAcceptedCursorBySource` acknowledges the last eligible cursor in request
order for each source, including duplicates. An invalid request fails validation
as a whole.

## Development workspace status and selections

`GET /api/development-workspaces/{id}?organizationId={id}` can report
`vms[].startupStage` for the VM's current startup operation: `preparing_vm`,
`preparing_storage` (primary only), `setting_up`, or `checking_readiness`.
An absent stage means the precise startup step is unknown. A blocked start
can report its last recorded stage; only a ready VM phase confirms readiness.

### Dev environment resources, folder sizes and folder deletion

Each VM response can include `resources`, the latest CPU, memory and disk sample
the dev environment reported with its status heartbeat. `resources.state` is
`current`, `stale` (no fresh sample for 120 seconds), `historical` (the VM is not
running; values are the last known sample) or `unsupported` (the image predates
resource telemetry). Samples are bounded and validated by the control plane,
belong to one VM generation, and never start compute. `filesystems[]` reports
`/` and `/workspace` available/total bytes and file slots (inodes);
`diskUsageSupported` and `directoryDeletionSupported` reflect the image's
advertised capabilities.

`GET /api/development-workspaces/{id}/vms/{vmId}/resource-history?organizationId={id}`
returns the last hour of that VM's samples for workspace readers (`apps:read`).
It reads only stored control-plane data and never contacts the VM. The response
is `{ "vmId", "generation", "windowStart", "windowEnd", "samples": [...] }`, with
`Cache-Control: no-store`. `samples` holds at most 60 entries in ascending order:
`minute` (UTC minute), `observedAt`, `cpuUtilizationPercent`, `memoryUsedBytes`,
`memoryTotalBytes`, `workspaceDiskUsedBytes` and `workspaceDiskTotalBytes`. Any
metric may be `null` when the reading was unavailable.

- Collection: the worker records at most one sample per VM generation per minute
  (the newest observation wins) from its verified status heartbeats, so collection
  continues without dashboard viewers.
- Generation: only samples from the VM's current `generation` are returned, so a
  restarted VM starts with an empty chart. A stopped VM keeps returning its last
  hour until the samples expire.
- Retention: samples expire after two hours and are deleted in bounded batches.
- Cleanup: history is deleted when the workspace is deleted or a temporary VM is
  discarded.
- Errors: an unknown VM, or a VM in another workspace, returns `404`.

Folder sizes are measured only on request. All folder endpoints require
organization app-write permission (`apps:write` for API keys), a running and ready
VM with the matching `generation`, an image that advertises the capability, and an
`idempotencyKey`. Responses are `Cache-Control: no-store`; one folder job runs at a
time per VM (`409 workspace_filesystem_job_active`).

- `POST /api/development-workspaces/{id}/vms/{vmId}/disk-usage/scans?organizationId={id}`
  with `{ "generation", "idempotencyKey", "targetToken"? }` returns `202` with a
  folder job. Omit `targetToken` to measure `/workspace`; pass an entry's
  `targetToken` from a completed scan to measure that child folder.
- `POST /api/development-workspaces/{id}/vms/{vmId}/directory-deletions?organizationId={id}`
  with `{ "generation", "targetToken", "confirmationPath", "acknowledgePermanentLoss": true, "idempotencyKey" }`
  returns `202`. `confirmationPath` must exactly equal the entry's full `path`.
  The request and its outcome are audited.
- `GET .../vms/{vmId}/filesystem-jobs/{jobId}` polls a job;
  `POST .../filesystem-jobs/{jobId}/cancel` requests cancellation.

Scan results list immediate child folders by allocated size with `state`
`complete`, `partial` (at least this size), `unreadable` or `excluded` (separate
disk); unknown sizes are `null`, never `0`. `filesHereBytes` covers files directly
in the folder. Target tokens are opaque and expire with `targetsExpireAt`, five
minutes after the scan completes or as soon as a later deletion is requested; then
`workspace_folder_selection_expired` requires a new scan. Filesystem device/inode
identities are never returned. Only folders at least two levels inside
`/workspace/repos`, `/workspace/home` or `/workspace/artifacts`, owned by the dev
environment user and on the same disk, have `canDelete: true`; others report a
`protectedReason`. Deletion jobs finish in `succeeded`, `partially_deleted`,
`failed`, `cancelled` or `outcome_unknown`. Deletion is permanent and backups are
not an undo.

Workspace list/detail responses include optional `createdBy` (the workspace
creator's actor ID). The dashboard uses it only to present primary T3 Connect
management to the creator or an organization Owner. The Connect launch and
gateway endpoints independently reauthorize the current dashboard identity;
this field does not confer authority and ordinary workspace managers do not
automatically receive account-linking permission.

`GET /api/development-workspaces/selections?organizationId={id}` requires
authentication and organization app-read permission (`apps:read` for API keys).
Without `workspaceId`, it returns only qualified T3 Connect images explicitly
approved for new dev environments.
Add `&workspaceId={id}` for next-start resize or ephemeral launch choices: these
must remain in that workspace's fixed provider account and region. A missing or
foreign workspace returns `404 workspace_not_found`; organization access denial
returns 403, and anonymous requests return 401.

The uncached response is:

```json
{
  "available": true,
  "unavailableReason": null,
  "options": [
    {
      "imageId": "example-connect-revision",
      "imageName": "Muni T3 with Connect",
      "profile": "standard",
      "planId": "plan_example",
      "planName": "Development standard",
      "providerAccountName": "Primary Hetzner",
      "region": "nbg1",
      "sku": "cx-example",
      "cpuCount": 4,
      "memoryGiB": 8,
      "computeHourlyRate": 0.1,
      "isDefault": true,
      "agents": [
        { "id": "codex", "name": "Codex", "version": "0.169.0" },
        { "id": "claude", "name": "Claude Code", "version": "2.1.328" }
      ]
    }
  ],
  "currency": "EUR",
  "volumeMonthlyRatePerGb": 0.0605
}
```

`cpuCount`, `memoryGiB` and `computeHourlyRate` come from the approved profile;
`currency` and `volumeMonthlyRatePerGb` come from the pricing policy and are
`null` when unavailable. They are configured estimates, not invoices. The portal
uses them to show a price before creation. `isDefault` is `true` only for
unscoped (new environment) discovery when the option's plan is listed in
`DevelopmentWorkspaces:Profiles:{profile}:DefaultPlanIdsForNewWorkspaces`. The
portal preselects one default option, or the only option. If there are several
options and not exactly one default, it asks the user to choose.

Submit `imageId`, `profile` and `planId` unchanged to the existing create, resize
or ephemeral-launch write contracts. IDs are stable configuration/plan identities,
not labels or option indexes; labels may change. Configure the optional
`DevelopmentWorkspaces:Images:{imageId}:DisplayName` for a human-readable image
label. Omitted or blank labels display the image configuration key (`imageId`);
supplied labels are trimmed.

`agents` lists the coding agents installed in the image, from
`DevelopmentWorkspaces:Images:{imageId}:Agents` (copied from the image build
manifest). Workspace list and detail responses include the same `agents` for the
primary VM's recorded image. The field is omitted when the operator has not
configured agents for the image; it is informational and never affects
eligibility. Agents still need a sign-in inside T3.

Discovery shares selection eligibility with writes: explicitly enabled,
runtime-qualified amd64 images with a valid manifest and qualification timestamp;
approved 8/16 GiB profiles with positive CPU/pricing and explicit plan, SKU and
region allowlists; and enabled organization-owned Hetzner, DigitalOcean or Vultr
accounts/plans with matching image account, architecture and regions. Cross-organization entries and
credential references are never returned. Discovery only reads control-plane
metadata: it performs no credential resolution, provider requests, reservation,
allocation or mutation.

For new environments, the image entry must also have
`DevelopmentWorkspaces:Images:{imageId}:T3ConnectForNewWorkspaces` set to `true`.
Create rejects stale or unapproved image selections with `workspace_image_invalid`;
there is no legacy fallback. This approval does not affect workspace-scoped
selections or restart on an existing VM's recorded image, and does not link a
T3 account. See [image registration](t3-connect.md#registering-an-image-for-new-dev-environments).

- `available: true` with an empty `options` array means configuration is available
  but no compatible selections exist for this organization/placement (for
  unscoped discovery, no approved Connect image has a compatible plan).
- `available: false`, a human-readable `unavailableReason` and empty `options`
  means workspaces are disabled or required quota/pricing/lease configuration is
  unavailable or invalid (including stale pricing).
- Catalog database/time-out failures return sanitized HTTP 503
  `workspace_selections_unavailable`, not an empty catalog. Other HTTP failures
  remain failures, not configuration states.

Discovery is advisory, not a capacity or credential-availability guarantee.
Writes revalidate current selection and placement policy. Creation leaves the
workspace stopped (unless `startPrimaryVm` is set; see below). Starting or launching compute checks VM concurrency; provider
quotas still apply. Projected cost is informational at every amount: creation,
manual or queued starts and temporary VM launches never reject a request
because of estimated spend, and responses keep returning `computeHourlyRate`,
`estimatedMonthlyCompute` (the hourly rate × 720 hours) and
`estimatedMonthlyStorage`. The former `workspace_spend_limit` error code is no
longer emitted. Neither operation limits the number of workspace records or the
number of distinct active workspaces. Resize records a next-start selection
without reserving compute; start revalidates it again. Existing idempotency,
generation and repository-approval requirements are unchanged.

### Create and start the primary VM

`POST /api/development-workspaces` accepts optional `"startPrimaryVm": true`.
Omitted or `false` keeps the default: the workspace is created stopped, and
existing callers (including the CLI) and their idempotent retries are
unchanged. When `true`, the create also queues one start of the primary VM in
the same transaction, applying the normal start admission. The field is part
of the idempotent request, so a retry with the same `idempotencyKey` returns
the same workspace and the same start operation without queueing another;
reusing the key with a different `startPrimaryVm` returns
`409 workspace_idempotency_conflict`.

If the start isn't admitted (for example `workspace_admission_limit`), the
workspace is still created with its primary VM stopped, and the response
returns `201` with the reason in the primary VM's `errorCode` and
`errorMessage`. Retry with the normal VM start route; a successful start clears
the reason. The dashboard uses this when creating a dev environment, then
opens the new environment's detail page to follow startup. Viewing or
refreshing that page never starts a VM.

The dashboard saves an outstanding create's idempotency key in browser session
storage before sending it. If the result is uncertain, submitting the same
selection and name again in that tab reuses the key, even after closing the
dialog or reloading the page. A different request gets its own key. Once the
dashboard receives the created environment and opens its detail page, the key
is cleared. Browser session storage must be available; if saving the key fails,
the dashboard sends no create request. Closing the tab or clearing its storage
loses outstanding retry keys; check the environment list before creating again.

## Development workspace idle timeout

Workspace list and detail responses include `idleTimeoutMinutes` (the saved
whole-minute override, or `null` to inherit) and `effectiveIdleTimeoutMinutes`
(the whole-minute timeout in use). They use the existing organization app-read
authorization. `POST /api/development-workspaces` accepts optional
`idleTimeoutMinutes`; omission or `null` inherits and remains compatible with
existing create callers and idempotent retries.

`PUT /api/development-workspaces/{id}/idle-timeout?organizationId={id}` takes
`{"idleTimeoutMinutes":90}` and returns the updated workspace. Send explicit
`null` to restore inheritance. The field is required on update, and overrides
must be JSON integers from 1 through 10,080 minutes (7 days); zero does not
disable shutdown. Updates use the existing workspace-management authorization
(organization app-write, `apps:write` for API keys) and browser CSRF protection.

Each VM uses its workspace's effective timeout independently: the saved override
wins, then `DevelopmentWorkspaces:IdleGraceMinutes`, then 360 minutes (6 hours).
The timeout measures inactivity, not maximum VM lifetime. Changing it applies
to running and future VMs, can cancel a premature unclaimed idle countdown,
and does not undo a claimed shutdown. A shorter timeout lets eligible VMs enter
the normal countdown on the next monitor pass. The countdown and gateway drain
are separate. Shutdown deletes compute while retaining primary storage; compute
charges continue until deletion is confirmed, and a longer timeout may increase
cost. Dashboard users manage the value in **Workspaces → workspace → Settings**.

### Keep running

Workspace responses include `keepRunning`. When it is `true`, automatic idle
stop is off for every machine in the dev environment and
`effectiveIdleTimeoutMinutes` is `null`; `idleTimeoutMinutes` keeps the saved
override, if any. Set it with
`{"idleTimeoutMinutes":90,"keepRunning":true}` on the same `PUT …/idle-timeout`
route, or with `"keepRunning": true` on create. Omitting `keepRunning` or
sending `false` turns automatic idle stop back on with the supplied timeout, so
existing callers and idempotent create retries are unchanged. Turning Keep
running on cancels unclaimed idle countdowns and does not undo a claimed
shutdown. Automatic idle-stop admission returns `409 workspace_keep_running`.

Keep running does not start a stopped machine or bypass safeguards. Manual
stop and stop-all, task leases, Git-save guards, generation fencing, quotas
and VM concurrency limits apply unchanged. Compute is billed for every running
machine until it is stopped and deletion is confirmed.

## Permanent development workspace deletion

Send `DELETE /api/development-workspaces/{id}?organizationId={organizationId}&confirmPermanentDeletion=true`
with no request body. Explicit confirmation is required. This is irreversible:
unlike stopping compute, deletion removes the primary retained volume and any
remaining local VM data. Stop all workspace VMs and wait for compute cleanup
to finish before requesting deletion.

The response body is the normal workspace representation. `202 Accepted` means
deletion is in progress or a retry has been queued; `200 OK` means the workspace
is deleted, including on repeated requests for an already-deleted workspace.
Read the top-level `phase` (`deleting`, `delete_failed`, or `deleted`) and
top-level `errorCode` and `errorMessage` for failure details. A `deleting`
phase indicates permanent deletion only when `volumeStatus` is also `deleting`;
ordinary compute cleanup can report the same phase while retaining the volume.

Check progress with `GET /api/development-workspaces/{id}?organizationId={organizationId}`.
If the phase is `delete_failed`, address the reported error and repeat the
confirmed DELETE to retry. The organization-scoped list excludes deleted
workspaces; the authorized GET by ID still returns the deleted tombstone. Once
deletion completes, its name and create `idempotencyKey` can be used for a new
workspace.

## Browser accounts

The dashboard uses credentialed requests only to its configured API origin.
Session cookies are Secure, HttpOnly and host-only. Session and CSRF secrets are
not stored in browser storage. `/api/me` retains its existing organization
membership response for both browser users and authorized machine credentials.

| Route | Behavior |
| --- | --- |
| `GET /api/account/capabilities` | Password/optional-method availability, whether **Keep me signed in** is offered (`keepSignedIn`), and authoritative `required`, `ready` or `blocked` bootstrap state. |
| `GET /api/account/session` | Browser user ID, absolute/idle expiry, `renewAfter`, `keepSignedIn` (this session) and `keepSignedInPreference` (sign-in default), and the session-bound CSRF token. Reading the session does not extend it. |
| `POST /api/account/session/renew` | Explicit activity renewal. Inside the renewal window (`renewAfter`), extends the idle expiry (and the upstream Kratos session) up to the absolute expiry; earlier calls return the session unchanged. Never rotates the cookie or CSRF token and never restores a revoked session. The extension commits only if the session is still the one this cookie authenticated; see [session renewal](#session-renewal). `401` if it ended meanwhile, `503 auth_unavailable` on a Kratos outage, `409 session_conflict` if it kept changing concurrently (retry). |
| `PUT /api/account/session/preference` | Body `{keepSignedIn}`. Stores the user's preference and applies it to the current session's limits and cookie persistence, under the same commit-time checks as renewal. `409 keep_signed_in_unavailable` when the operator disabled the option; `401` if the session ended meanwhile or the standard limits already end it. |
| `POST /api/account/logout` | Revoke the browser session and Grafana access; clear authentication cookies without revoking API keys. |
| `GET /api/account/flows/{kind}/browser` | Start `login`, `settings` or enabled `recovery`; `returnTo` must be a same-dashboard relative path. |
| `GET /api/account/flows/{kind}?id={uuid}` | Read an existing matching form. |
| `POST /api/account/flows/{kind}?flow={uuid}[&keepSignedIn=true]` | Submit the form once; return a sanitized form or rotated browser-session metadata, never a session-cookie secret. |
| `POST /api/account/flows/{kind}/oidc?flow={uuid}&returnTo={path}[&keepSignedIn=true]` | Optional Google only (`login` or `settings` link). Body `{csrf_token, provider:"google"}`; returns `{provider, redirectUrl}` for explicit top-level navigation. |
| `GET /api/account/oidc/self-service/methods/oidc/callback/google` | Fixed provider callback. Completes the pending flow and answers `303` to the dashboard's `/sign-in/continue`. |

Settings and other cookie-authenticated mutations require exact dashboard Origin
and `X-Minicloud-CSRF`. Initial login/recovery submissions instead require exact
Origin and the Kratos form's CSRF field/cookie. Browser credentials never replace
machine authentication: explicit bearer credentials take precedence, including
invalid bearers. CLI approval still requires explicit human consent and
organization API-key-write permission.

### Session renewal

A renewal reads the session, extends the upstream Kratos session if needed, and
then commits the new idle deadline with one conditional update. The update
applies only if, at commit time, the row still has the request cookie's token
hash, is not revoked, and still has the persistence flag, absolute expiry, idle
deadline and last renewal time that the request read. Eligibility is
re-evaluated with the commit-time clock: the idle deadline and absolute expiry
have not passed and the session is inside its renewal window. The new deadline
is capped by the absolute expiry and by the upstream session's expiry.

- **Competing renewals** of one session coalesce. One request extends it; the
  others see that the renewal window has moved and return the already-renewed
  session unchanged with `200`. The local session is extended once. Kratos sets
  its expiry from the time of each extension rather than adding to it, so a
  repeated upstream extension does not lengthen the session.
- **Preference changes** use the same conditional update. Whichever commits
  first wins; a renewal that loses re-reads the session and applies the current
  policy.
- **Logout, revocation, rotation and expiry** made while a renewal waits on
  Kratos are never undone. If the session expired, the renewal ends it and
  returns `401`. If this renewal extended the upstream session and the local
  session ended meanwhile (logout, revocation or expiry), the API revokes the
  upstream session again so the late extension cannot keep it alive. This also
  happens when the extension times out or Kratos fails. A new sign-in on the
  same device replaces the token, and an older renewal leaves the new session and
  its upstream session untouched.
- A successful renewal never sets a cookie and keeps the CSRF token.

The dashboard runs one account request at a time per tab and uses a Web Lock so
one tab renews at a time; other tabs adopt its broadcast result. A sign-out
broadcast, a rejected renewal (`401`, or `403` after a rotation elsewhere), or a
network reconnect always triggers a fresh `GET /api/account/session`, queued
behind any request in flight. Only that read signs the tab out. A reconnect
never counts as user activity.

Account responses are not cacheable. Confirmed expiry is 401; authorization denial
remains 403/resource-hiding 404. Identity dependency failures are sanitized 503
`auth_unavailable`, not a logout or a setup transition. Forms have explicit restart
actions and mutations are never automatically replayed.

Public registration is disabled. Email recovery is optional and requires
separate SMTP/integration qualification; password-only use does not depend on
either optional method.

When the dashboard detects an expired sign-in form, it requests a fresh browser
flow. It does not resubmit a password or start a Google redirect automatically;
the user continues sign-in from the new form.

### Optional Google sign-in

Google is off by default (`Kratos:GoogleEnabled=false`). The packaged installer
supports deliberate opt-in through `googleOidc.enabled` and a protected
credential-input file; see [packaged setup](self-hosting.md#optional-packaged-google-oidc).
Configuration enables the option, not a live qualification claim. The current
API has no separate `GoogleQualified` setting. Live Google and separate-origin
HTTPS qualification remain
tracked in [the authentication follow-up](https://github.com/muniventures/minicloud/issues/44).

- **Discovery.** `ui.providers` is present only when Google is enabled and the
  Kratos flow offers it. Each entry is `{id:"google", action, method:"POST"}`;
  `action` is exactly `/api/account/flows/{kind}/oidc?flow={id}`. Upstream OIDC
  nodes are never forwarded, and the ordinary submit route rejects `provider`.
- **Navigation.** The dashboard shows "Continue with Google" or "Link Google
  account" only when both the capability and the provider entry are present.
  A click posts the flow CSRF token to the provider action with the exact
  dashboard Origin (`settings` also needs `X-Minicloud-CSRF`). The API submits
  the Kratos `oidc` method and accepts only Kratos's 422
  `browser_location_change_required` response. Its target must be
  `https://accounts.google.com/o/oauth2/...` on the default port, with no user
  info or fragment. It must have one `client_id`, one `state`,
  `response_type=code`, and a `redirect_uri` equal to this API's fixed callback.
  Anything else returns 503 `auth_unavailable`. The browser, not the API, then
  opens the URL. The API never follows upstream redirects or contacts the
  provider, and it forwards no Minicloud cookie or bearer.
- **Callback.** The provider action sets a 10-minute `__Host-minicloud-oidc`
  cookie. It records the kind, flow ID, a SHA-256 hash of the provider state
  and the validated same-dashboard return path. The callback requires that
  cookie and a matching `state`, then deletes the cookie. It forwards only
  `state` with either `code` or `error` to the Kratos callback, with allowlisted
  flow cookies, including `ory_kratos_continuity`. It then admits only the bound
  identity, as for password login. Settings must match the current browser
  user. The callback redirects to
  `/sign-in/continue?result=success|failed|forbidden|unavailable&kind=...&returnTo=...`.
  On failure, the redirect can include `flow` for the same pending flow. The
  dashboard then re-reads it, or after success returns to the safe path, such
  as explicit CLI consent.

The installer derives `MINICLOUD_GOOGLE_OIDC_ENABLED` from the explicit
configuration and Compose passes it to both `Kratos__GoogleEnabled` on the API
and `SELFSERVICE_METHODS_OIDC_ENABLED` on Kratos (both default false).
`SELFSERVICE_METHODS_OIDC_CONFIG_BASE_REDIRECT_URI` is always derived as
`<API_URL>/api/account/oidc`; the resulting Google redirect URI is
`<API_URL>/api/account/oidc/self-service/methods/oidc/callback/google`.
The installer imports only the fixed `google` provider into the protected
`kratos.env` as `SELFSERVICE_METHODS_OIDC_CONFIG_PROVIDERS`, using the bundled
read-only verified-email Jsonnet mapper. Unknown environment/configuration
fields still fail validation; manually adding enable/callback overrides to
`kratos.env` is not supported. Minicloud ships no default client or secret.
Google login admits only identities already bound to a Minicloud user. An
operator first links Google from account settings; public registration stays
disabled. Keep the public API Host intact through the reverse proxy.
