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

## Required behavior

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
