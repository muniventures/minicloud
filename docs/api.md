# Selfrun API

Selfrun uses one unversioned first-party namespace: `/api`.
The prelaunch application/environment cutover deliberately removes old product
API routes and identity aliases. Clients and server must be updated together.

- `/api/apps` lists/creates applications with environment summaries.
- `/api/apps/{applicationId}` reads/updates/deletes an application.
- `/api/apps/{applicationId}/environments` lists/creates its environments.
- `/api/apps/{applicationId}/environments/{environmentId}` reads/updates/deletes
  one environment, with ownership and lifecycle checks.
- The environment response includes its sleep policy and power state. The same
  route accepts revision-checked `sleep.policy` updates; manual operations use
  `POST /api/apps/{applicationId}/environments/{environmentId}/sleep` and
  `/wake`. Policy changes require `apps:write`; sleep and wake require
  `deployments:create`.
- `/api/environments/{environmentId}` returns the runtime descriptor used by
  operational clients. Its telemetry, logs, storage and image actions use the
  same explicit environment identity. Plan and preview actions also target this
  environment resource; they do not resolve application aliases.
- Other product resources use `/api`, including providers, plans, deployments,
  secrets, runtime ingestion, registry credentials and CLI authentication.

Anonymous `GET /api/instance` returns `product`, `protocolVersion` and `appDomain`. The product value is `selfrun`.
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
those external protocols and remain unchanged. They are not parallel Selfrun
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

## Environment sleep

`GET /api/apps/{applicationId}/environments/{environmentId}` includes `sleep`:
the `policy` (`sleepAfterMinutes`, `httpTrafficIsActivity`, and `revision`),
power `state`, activity timestamps, `sleepsAt`, eligibility, derived
`addressMode`, retained-address and snapshot summaries, the latest power
`operation`, and any `lastError`.

PATCH accepts a nested policy with the current revision:

```json
{
  "sleep": {
    "policy": {
      "enabled": true,
      "sleepAfterMinutes": 120,
      "httpTrafficIsActivity": true,
      "revision": 0
    }
  }
}
```

Omitted policy fields keep their current values. `enabled: false` or
`sleepAfterMinutes: null` disables sleep. Enabling without a timeout defaults to
120 minutes on first enable and keeps the existing timeout on later enables.
Timeouts range from 30 to 43,200 minutes. A stale revision returns
`409 sleep_policy_revision_conflict`; invalid values return
`400 sleep_policy_invalid`. In retained address mode, policy enable reserves the
address before saving the policy, and a failed reservation returns
`address_reservation_failed`.

`POST .../sleep` requests immediate sleep and returns `202` with the queued
operation, or `409 environment_sleep_ineligible` / `environment_power_busy`.
`POST .../wake` accepts an optional `{ "keepAwakeMinutes": 240 }` body (30–1,440
minutes). It returns `202` with the wake operation, or `200` with the sleep
state after recording keep-awake activity when the environment is already
awake. A wake request joining a queued/running wake returns that operation.
When sleep is already running, wake is durably queued behind it and returns
`202`; when sleep is queued but the environment is still awake, the keep-awake
request prevents that sleep from reaching provider power-off.
Sleeping or waking environments reject server-dependent operations with
`environment_asleep` or `environment_waking` and a wake hint.

## HTTP activity observations

`POST /api/runtime/ingest/status` accepts optional `httpRequests` entries
containing only a configured `hostname` and its `lastRequestAt` timestamp.
The agent excludes Selfrun health probes and unknown hosts locally; ingestion
also checks the hostname against the runtime's current configured routes and
ignores missing observations, observations older than one minute, and future
observations beyond the five-minute clock-skew allowance. In-skew future times
are clamped to receipt time. This signal is stored in the environment sleep
record regardless of `httpTrafficIsActivity`; it updates idle activity only
when that policy setting is enabled. Request paths, headers, bodies, and client
addresses are not included.

## Browser accounts

The dashboard uses credentialed requests only to its configured API origin.
Session cookies are Secure, HttpOnly and host-only. Session and CSRF secrets are
not stored in browser storage. `/api/me` retains its existing organization
membership response for both browser users and authorized machine credentials.

| Route | Behavior |
| --- | --- |
| `GET /api/account/capabilities` | Password, passkey (`passkey`), Google, and email-recovery availability, whether **Keep me signed in** is offered (`keepSignedIn`), and authoritative `required`, `ready` or `blocked` bootstrap state. |
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
and `X-Selfrun-CSRF`. Initial login/recovery submissions instead require exact
Origin and the Kratos form's CSRF field/cookie. Browser credentials never replace
machine authentication: explicit bearer credentials take precedence, including
invalid bearers. CLI approval still requires explicit human consent and
organization API-key-write permission.

For password and passkey changes, a successful settings submission rotates the
current browser session and revokes older browser and Grafana sessions. If the
password is rejected (`400 auth_password_rejected`), choose a different password
and retry the form. For `410 auth_flow_expired`, start a new settings form. For
`403 session_refresh_required`, re-authenticate in place through a fresh `login`
flow (or log out and sign in again) and start a new settings form; the API does
not expose Kratos redirects or resume the previous submission.

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
tracked in [the authentication follow-up](https://github.com/muniperez/selfrun/issues/44).

- **Discovery.** `ui.providers` is present only when Google is enabled and the
  Kratos flow offers it. Each entry is `{id:"google", action, method:"POST"}`;
  `action` is exactly `/api/account/flows/{kind}/oidc?flow={id}`. Upstream OIDC
  nodes are never forwarded, and the ordinary submit route rejects `provider`.
- **Navigation.** The dashboard shows "Continue with Google" or "Link Google
  account" only when both the capability and the provider entry are present.
  A click posts the flow CSRF token to the provider action with the exact
  dashboard Origin (`settings` also needs `X-Selfrun-CSRF`). The API submits
  the Kratos `oidc` method and accepts only Kratos's 422
  `browser_location_change_required` response. Its target must be
  `https://accounts.google.com/o/oauth2/...` on the default port, with no user
  info or fragment. It must have one `client_id`, one `state`,
  `response_type=code`, and a `redirect_uri` equal to this API's fixed callback.
  Anything else returns 503 `auth_unavailable`. The browser, not the API, then
  opens the URL. The API never follows upstream redirects or contacts the
  provider, and it forwards no Selfrun cookie or bearer.
- **Callback.** The provider action sets a 10-minute `__Host-selfrun-oidc`
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

The installer derives `SELFRUN_GOOGLE_OIDC_ENABLED` from the explicit
configuration and Compose passes it to both `Kratos__GoogleEnabled` on the API
and `SELFSERVICE_METHODS_OIDC_ENABLED` on Kratos (both default false).
`SELFSERVICE_METHODS_OIDC_CONFIG_BASE_REDIRECT_URI` is always derived as
`<API_URL>/api/account/oidc`; the resulting Google redirect URI is
`<API_URL>/api/account/oidc/self-service/methods/oidc/callback/google`.
The installer imports only the fixed `google` provider into the protected
`kratos.env` as `SELFSERVICE_METHODS_OIDC_CONFIG_PROVIDERS`, using the bundled
read-only verified-email Jsonnet mapper. Unknown environment/configuration
fields still fail validation; manually adding enable/callback overrides to
`kratos.env` is not supported. Selfrun ships no default client or secret.
Google login admits only identities already bound to a Selfrun user. An
operator first links Google from account settings; public registration stays
disabled. Keep the public API Host intact through the reverse proxy.

### Passkey sign-in and settings

Passkey support is gated by `Kratos:PasskeyEnabled` (default `false` in raw API
options; default `true` in the packaged self-hosting Compose bundle via
`SELFRUN_PASSKEY_ENABLED`) and exposed via `GET /api/account/capabilities` as
`passkey`.

- **Discovery and sanitization.** When `Kratos:PasskeyEnabled` is `true`,
  `login` and `settings` flows include a sanitized `ui.passkey` object rather
  than forwarding raw Kratos `passkey` or `webauthn` nodes (upstream Kratos
  `script` nodes are never forwarded):
  - On `login`: `ui.passkey.loginOptions` contains validated WebAuthn
    `PublicKeyCredentialRequestOptions` JSON derived from Kratos's
    `passkey_challenge`.
  - On `settings`: `ui.passkey.registrationOptions` contains validated WebAuthn
    `PublicKeyCredentialCreationOptions` JSON derived from
    `passkey_create_data`, and `ui.passkey.credentials` lists enrolled passkeys
    (`id`, `displayName`, `addedAt`, `canRemove`).
- **Submission.** The browser executes `navigator.credentials.get` or
  `navigator.credentials.create` directly against the HTTPS dashboard origin and
  submits the serialized JSON response via `POST /api/account/flows/{kind}` with
  exact dashboard Origin, upstream flow CSRF token (`settings` also requires
  `X-Selfrun-CSRF`), `method: "passkey"`, and exactly one of:
  - `passkey_login` (`login` flow only) — verified against Kratos and admitted
    only for an already-bound Selfrun user, respecting `keepSignedIn`.
  - `passkey_settings_register` (`settings` flow only) — enrolls an additional
    passkey and rotates the browser session on success.
  - `passkey_remove` (`settings` flow only) — removes a specific credential ID
    and rotates the browser session on success.
  When passkeys are disabled, or when unexpected/mixed passkey fields are sent,
  the API rejects the request with `400 auth_flow_invalid` without calling
  Kratos.
- **Re-authentication.** When Kratos enforces `privileged_session_max_age` on a
  passkey settings change (`403 session_refresh_required`), the API preserves
  the existing browser session and does not expose Kratos redirects. The
  dashboard offers an explicit in-place re-authentication flow
  (`GET /api/account/flows/login/browser`, which requests `refresh=true` with
  the active session) supporting either passkey or password confirmation.
