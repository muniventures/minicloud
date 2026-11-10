# T30 — Identity, browser sessions and shared bootstrap

Status: not started. Repository: public `muniventures/minicloud`.
Depends on: T00 contracts and ownership map.
Can run in parallel with: T10 DB work, T20 CLI distribution and T40 images; T41 packaging after the identity/schema/bootstrap contract is frozen. Shared backend model writers are serialized **T30 → T50 → T60**.

Read the [feature overview](../1.pre-v1-release.md), [execution plan](../0.execution-plan.md), repository instructions and [self-hosting contract](../../v1.0-self-hosted-installation.md). This task is self-contained for public-repository cloud agents; it requires no private checkout, live host or operator credential.

## Assignment and ownership

Replace Firebase browser authentication with instance-local Kratos and opaque API sessions. Implement the one shared bootstrap/seed and host-recovery service used by installation and browser setup.

Own `modules/api/Auth/`, account/auth controllers and wiring, identity/session/bootstrap models and services in `modules/common/`, matching `modules/contracts/Contracts/` DTOs, a forward migration and focused API/service tests. Reserve the DbContext/model snapshot and shared actor/mapping files through T00; hand them off before T50 starts. Inspect current Firebase consumers before removing dependencies.

Do not edit dashboard `AuthProvider`, startup runtime configuration, server installer assets or hosted workflows. T31 owns ordinary dashboard auth, T41 owns runtime configuration and packaging, and T42 owns first-operator setup screens and their API transport. T42 must call this task's shared bootstrap service, never create a second seed implementation.

## Contract to freeze first

- Publish canonical `/api/account/*` routes, session/CSRF DTOs, error shapes, cookie rules, browser-flow proxy allowlist and capabilities consumed by T31/T41/T42. Preserve `/api/me`, CLI approval/key-login contracts and independent machine routes.
- Define the shared seed command/service input and result contract: installation and bootstrap-operation identity, admission/completion state, protected noninteractive input, interruption reconciliation and explicit host recovery. Secrets use protected files/stdin or authenticated HTTPS, never command-line arguments or logs.
- Preserve immutable internal user/workspace IDs and current membership authorization. Existing-operator conversion requires explicit verified internal identities and a new password; email matching is not identity proof.

## Frozen T00 wire and seed contract

These are implementation requirements, not routes already present on `main`. JSON uses existing camel-case serialization, UTC ISO-8601 timestamps and `ErrorResponse`/`ErrorBody`/`ErrorDetail` from `modules/contracts/Contracts/ErrorDtos.cs`. Add `AccountDtos.cs` and `BootstrapDtos.cs` in that contracts directory. Do not copy upstream identity administration models into customer DTOs.

| Method and canonical route | Request / response and admission |
| --- | --- |
| `GET /api/account/capabilities` | Anonymous, no-store. `AccountCapabilitiesResponse { password:true, google:boolean, emailRecovery:boolean, bootstrapState:"required"|"ready"|"blocked" }`. Unknown populated state is `blocked`, never `required`; transient dependency failure is 503 `auth_unavailable`, not a bootstrap-state transition. No identity/user inventory is exposed. |
| `GET /api/account/session` | `AccountSessionResponse { userId, expiresAt, idleExpiresAt, csrfToken }` for a valid browser session; 401 `unauthenticated` otherwise. Valid machine credentials do not become browser sessions. `/api/me` retains its existing `MeResponse` and organization-membership semantics for both authorized credential types. |
| `POST /api/account/logout` | No body. Browser session + CSRF/Origin; revoke local/upstream human sessions as applicable and Grafana access, clear session and allowlisted Kratos cookies; 204. No API-key revocation. |
| `GET /api/account/flows/{kind}/browser` | Start `login`, `settings`, or enabled `recovery`; only optional `returnTo` (relative same-dashboard path including query). `settings` requires a browser session. Return `AccountFlowResponse { flow, session:null }`. |
| `GET /api/account/flows/{kind}?id=<uuid>` | Read an existing matching flow. Same enabled-kind/admission checks; return the flow envelope. |
| `POST /api/account/flows/{kind}?flow=<uuid>` | Submit bounded JSON browser-flow fields, preserving upstream flow CSRF. Successful verified login/password change returns `{flow:null, session:AccountSessionResponse}` and rotates the Minicloud cookie. Intermediate recovery/optional OIDC steps return the flow envelope. |

`AccountFlowResponse` has exactly one non-null member. `flow` is `AccountBrowserFlow { id, kind, expiresAt, ui }`; `ui` contains sanitized Kratos browser nodes/messages required by enabled methods, with an API-proxy action URL and method, never an admin/upstream-origin action. T30 uses typed, bounded browser-flow JSON models; T31 renders supported nodes, does not execute supplied HTML/scripts, and rejects unknown action origins. Optional OIDC navigation, if enabled, is an explicit validated HTTPS provider redirect in the flow UI, not an automatically followed proxy redirect.

Proxy translation is fixed: the above start/read/submit routes map respectively to Kratos public `/self-service/{kind}/browser`, `GET /self-service/{kind}/flows?id=...`, and `POST /self-service/{kind}?flow=...`. Only those method/path combinations and validated IDs/return targets are accepted. No arbitrary path/URL proxy, registration, admin, native/API login flow, or raw upstream response is exposed. Cap request bodies at 64 KiB, use a 10-second upstream timeout, disable automatic redirects and shared cookie containers, and rewrite only configured Kratos flow cookies for the API host with secure host-only scope. Initial login/recovery submission uses Kratos flow CSRF plus exact dashboard Origin, not a nonexistent local session CSRF token. Cookie-authenticated settings and other unsafe API calls also require `X-Minicloud-CSRF`.

Flow failures use the repository error envelope: 400 `auth_flow_invalid` with sanitized field details, 401 `unauthenticated` for rejected/expired authentication, 403 `forbidden` or `csrf_invalid`, 410 `auth_flow_expired` with an explicit restart action in the UI, and 503 `auth_unavailable` for identity dependency failure. Clients may re-fetch a valid flow after field errors; never replay a password/mutation automatically. Account responses, including failures, are no-store.

The local cookie name is `__Host-minicloud-session`: Secure, HttpOnly, SameSite=Lax, path `/`, no Domain. It contains at least 256 bits of random secret; persist only its hash, bound immutable user/upstream session IDs and CSRF verifier. `X-Minicloud-CSRF` is session-bound and is obtained only through a credentialed session response. Absolute/idle defaults remain 12 hours/120 minutes. Allow credentialed CORS only from the configured exact dashboard origin; never wildcard it. A supplied bearer, even malformed/invalid, wins over cookies and cannot fall back to them. Upstream validation unavailability preserves the local session while blocking human requests with 503.

T31's `AuthProvider` exports `status:"initializing"|"authenticated"|"unauthenticated"|"unavailable"|"setup_required"|"setup_blocked"`, `profile:MeResponse|null`, `capabilities:AccountCapabilitiesResponse|null`, and explicit `refreshSession`/`logout` operations. CSRF stays in the central API transport's memory, not persisted browser storage. Setup state comes only from the capabilities/bootstrap service, never from an empty organization list or a failed `/api/me`.

T41 owns startup-loaded `/runtime-config.json`, read and validated before React mounts. Its public `RuntimeConfig` retains `apiBaseUrl` (origin without `/api`), `appDomain`, `sourceUrl`, and replaces `firebase` with `auth:{origin,capabilitiesUrl}`. `auth.origin` must equal the API origin; `capabilitiesUrl` is that origin plus `/api/account/capabilities`. Runtime capabilities come from that endpoint, not stale build-time flags. There is no separate public Kratos/admin origin. T31 can consume this shape without owning the config/startup files.

### One shared bootstrap and recovery implementation

T30 owns a `BootstrapService` under `modules/common/Services/` and a host entry point in the API executable, invoked by installer/manual maintenance as `bootstrap <operation> --input-file <protected-path>` or `bootstrap <operation> --stdin`. T30 owns the API startup dispatch needed for this entry point; coordinate dependency wiring with T41. This is not a new customer CLI command family or HTTP recovery API. Never accept passwords/tokens as argument values.

- Persist one installation-bound bootstrap record and stable `operationId` before calling Kratos. Record `installationId`, operation ID, state, token verifier/expiry, bound Kratos/internal user/organization IDs and completion timestamp. States are `required`, `in_progress`, `ready`, `blocked`; public capabilities collapse the first two to `required`, but unknown/populated or mismatched state to `blocked`. Completion checks precede secret validation, so a ready installer replay is a no-op without the original secret.
- Operations are `status`, `prepare`, `complete`, `rotate-token`, `recover-operator`, and explicit `bind-operator`. Common input is `{installationId,operationId}`. `prepare`/`rotate-token` use a 30-minute token lifetime, store only its verifier and reveal the token once through an explicitly protected output file or interactive terminal, never ordinary JSON/log output. Rotation is allowed only for the same unclaimed installation.
- `complete` adds `{setupToken,email,password,displayName,organizationName,organizationSlug}`. Return `BootstrapResult { installationId,operationId,state,userId?,organizationId?,completedAt? }`, never passwords/tokens. Browser transport supplies the verified setup admission; protected host input invokes exactly the same method. Serialize attempts and put the operation identity in private Kratos metadata so interrupted identity creation can be reconciled exactly. Uncertain upstream outcome blocks a fresh identity attempt until reconciled.
- `bind-operator` requires explicitly verified existing `userId`, `organizationId` and a new password through protected host input. Preserve IDs/memberships; require the declared owner relationship, never select the first user or match by email. This is the only existing-operator conversion path.
- `recover-operator` requires the ready installation's bound user/identity IDs and a new password through protected host input. Revoke affected browser/Grafana sessions, preserve memberships/API keys, and create no replacement user or organization. It requires host access, not SMTP.
- Consistent failures use typed service errors mapped to existing envelopes by HTTP transport: `setup_token_invalid` (401), `setup_token_expired` (410), `setup_in_progress`/`setup_already_completed`/`setup_state_conflict` (409), and `auth_unavailable` (503). Host failures have a nonzero exit code and sanitized error code/message. Host replay of completed `complete` is a no-op; browser token reuse is rejected as `setup_already_completed`.

T42 owns only `GET /api/account/setup` returning `BootstrapStatusResponse { state:"required"|"in_progress"|"ready"|"blocked", expiresAt? }`, and `POST /api/account/setup` accepting `CompleteSetupRequest` with the `complete` fields above except installation/operation IDs (loaded server-side). It uses exact Origin, bounded/rate-limited one-time admission and no-store responses; the setup token travels only in the HTTPS JSON body. On success return `BootstrapResult` and direct the UI to ordinary login, not a second session issuer. T30 implements all admission, hashing, state, seed and recovery logic; T42 adds no second migration or seed path.

Preserved CLI contract: anonymous `POST /api/cli-login-sessions`, `GET /api/cli-login-sessions/{sessionId}` and `POST /api/cli-login-sessions/{sessionId}/exchange`; human `POST /api/cli-login-sessions/{sessionId}/approve` with existing `{organizationId}`. Existing DTOs, expiry/one-time exchange and `WriteApiKeys` checks stay intact. Approval requires browser CSRF/Origin; anonymous machine polling/exchange does not. No Firebase token exchange or new CLI session protocol is introduced.

## Implementation requirements

1. Use bounded Kratos clients and a fixed API-origin browser-flow proxy. Keep Kratos administration private. Reject unapproved methods/paths, unsafe redirects and oversized bodies; do not follow upstream redirects or share cookie containers between users.
2. Bind immutable Kratos identity IDs to internal users. Never adopt privileged users by email, admit arbitrary users on login, recreate deleted owners or restore stale memberships.
3. Store opaque random browser-session secrets only as hashes, with user/upstream-session identity, CSRF binding, revocation, last-seen and expiry. Defaults: 12 hours absolute and 120 minutes idle. Validate upstream session activity on browser requests.
4. Use an HttpOnly, Secure, host-only API cookie, path `/`, SameSite=Lax. Rotate on login/password change. Keep secrets out of JavaScript/local storage and clear relevant Minicloud/Kratos cookies on logout.
5. Explicit bearer credentials take precedence; an invalid bearer must not fall through to cookies. Preserve scoped API keys, registry authentication, runtime identities, telemetry credentials and source-built CLI approval/key login.
6. Cookie-authenticated unsafe requests require session-bound CSRF and exact Origin validation. Preserve Kratos flow CSRF and initial login/recovery protection. Machine credentials and the CLI's existing anonymous create/poll/exchange flow must not acquire browser-CSRF requirements.
7. Return confirmed unauthenticated as 401, denied authorization as existing 403/resource-hiding 404, and temporary identity dependency failure as sanitized 503 `auth_unavailable`. Outage must not revoke a valid local session. Auth/session responses use `Cache-Control: no-store`.
8. Replace human `firebase` actor assumptions with `browser_session`, including Grafana human-session checks. Logout/password recovery revoke affected browser/Grafana sessions without changing independent API keys.
9. Fresh bootstrap creates exactly one identity, user, workspace and owner membership, without customer infrastructure. Serialize installation-bound attempts; reconcile interrupted identity creation by the exact bootstrap operation. Unknown populated state fails closed.
10. Completed bootstrap is a durable no-op without the original secret. Restart, rerun and upgrade cannot reset passwords or recreate owners. Provide explicit host-only recovery for the bound operator with browser-session revocation and no SMTP requirement.
11. Password-only installation needs no Firebase, hosted Ory account, Google or SMTP. Optional browser recovery/Google flows are capability-gated and separately qualified.
12. Remove migrated Firebase runtime dependencies/configuration after checking all consumers. Preserve historical migrations and unrelated customer examples. No fallback, dual authentication, password import or legacy route alias.

## Focused acceptance

Write and run regressions for session hashing/expiry/rotation/revocation, bearer-cookie ambiguity, CSRF/Origin rejection, proxy/admin exclusion, malicious return URLs and dependency outages. Verify CLI/API-key/registry/runtime/telemetry authentication remains usable during a Kratos outage.

Cover populated SQLite/PostgreSQL migrations preserving IDs/memberships and credentials; concurrent bootstrap; interruption immediately after identity creation; completion replay; unknown populated state; and explicit host recovery. Logs/errors must contain no passwords, setup tokens, cookies or upstream error bodies.

Use synthetic identities and local fixtures only. Report exact commands/results and missing environment capabilities. Codex owns final cross-feature integration and existing-operator cutover testing after every implementation PR merges; focused checks do not establish hosted qualification.

## Copilot assignment and handoff

Implement only T30 on current public `main`. Freeze the session/bootstrap contract before dependent agents proceed, keep one shared seed/recovery service, preserve machine authorization, and submit one focused PR with regression results and migration ordering. Update this task's status; give the integration owner its merged PR and remaining evidence gaps. Do not deploy, provision, bind the real operator or start downstream tasks.
