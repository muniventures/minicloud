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

Public registration is disabled. Google redirect/callback completion and
real-Kratos HTTPS qualification remain tracked in
[the authentication follow-up](https://github.com/muniventures/minicloud/issues/44).
Keep `Kratos:GoogleEnabled=false` until that implementation is complete. Email
recovery is optional and requires separate SMTP/integration qualification;
password-only use does not depend on either provider.
