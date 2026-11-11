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

## Browser accounts

The dashboard uses credentialed requests only to its configured API origin.
Session cookies are Secure, HttpOnly and host-only. Session and CSRF secrets are
not stored in browser storage. `/api/me` retains its existing organization
membership response for both browser users and authorized machine credentials.

| Route | Behavior |
| --- | --- |
| `GET /api/account/capabilities` | Password/optional-method availability and authoritative `required`, `ready` or `blocked` bootstrap state. |
| `GET /api/account/session` | Browser user ID, absolute/idle expiry and session-bound CSRF token. |
| `POST /api/account/logout` | Revoke the browser session and Grafana access; clear authentication cookies without revoking API keys. |
| `GET /api/account/flows/{kind}/browser` | Start `login`, `settings` or enabled `recovery`; `returnTo` must be a same-dashboard relative path. |
| `GET /api/account/flows/{kind}?id={uuid}` | Read an existing matching form. |
| `POST /api/account/flows/{kind}?flow={uuid}` | Submit the form once; return a sanitized form or rotated browser-session metadata, never a session-cookie secret. |
| `POST /api/account/flows/{kind}/oidc?flow={uuid}&returnTo={path}` | Optional Google only (`login` or `settings` link). Body `{csrf_token, provider:"google"}`; returns `{provider, redirectUrl}` for explicit top-level navigation. |
| `GET /api/account/oidc/self-service/methods/oidc/callback/google` | Fixed provider callback. Completes the pending flow and answers `303` to the dashboard's `/sign-in/continue`. |

Settings and other cookie-authenticated mutations require exact dashboard Origin
and `X-Minicloud-CSRF`. Initial login/recovery submissions instead require exact
Origin and the Kratos form's CSRF field/cookie. Browser credentials never replace
machine authentication: explicit bearer credentials take precedence, including
invalid bearers. CLI approval still requires explicit human consent and
organization API-key-write permission.

Account responses are not cacheable. Confirmed expiry is 401; authorization denial
remains 403/resource-hiding 404. Identity dependency failures are sanitized 503
`auth_unavailable`, not a logout or a setup transition. Forms have explicit restart
actions and mutations are never automatically replayed.

Public registration is disabled. Email recovery is optional and requires
separate SMTP/integration qualification; password-only use does not depend on
either optional method.

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
