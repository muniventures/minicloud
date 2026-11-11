# T31 — Dashboard sessions and ordinary account flows

Status: ordinary password/session/settings and capability-gated recovery implementation authored locally; not merged.
Optional Google button/callback continuation follows the T30 frozen extension and stays disabled by default. Live Google and real-Kratos qualification remain in [issue #44](https://github.com/muniventures/minicloud/issues/44).
Repository: public `muniventures/minicloud`.
Depends on: T30's frozen account/session/CSRF contract and backend implementation.
Can run in parallel with: T40/T41 packaging and non-overlapping SSH UI work. T42 setup UI follows both T31 and T41; no concurrent writer may change `AuthProvider`.

Read the [feature overview](../1.pre-v1-release.md), [execution plan](../0.execution-plan.md), [T30](30-identity-and-sessions.md), repository instructions and [self-hosting contract](../../v1.0-self-hosted-installation.md). Use merged canonical `/api` contracts rather than historical private examples.

T00 freezes the exact account routes, flow envelopes, CSRF header, auth-provider states and T41 runtime-config shape in [T30's wire contract](30-identity-and-sessions.md#frozen-t00-wire-and-seed-contract). Consume those names; do not invent an auth route alias or a different public Kratos origin.

## Assignment and ownership

Replace Firebase SDK authentication with API-backed browser sessions. Implement ordinary sign-in/out, session handling, password/account settings and capability-gated recovery while preserving CLI consent and organization authorization.

Own `modules/dashboard/app/core/auth/AuthProvider.tsx`, protected/guest guards, central API-client credentials/CSRF behavior, the account API client, ordinary auth/account screens and their routes, identity/logout consumers and focused tests. Reserve shared route edits through T00. Remove obsolete Firebase SDK mocks/dependencies after checking remaining consumers.

T41 exclusively owns `core/config/runtimeConfig.ts`, startup-loaded public configuration and dashboard image/startup plumbing. Consume its agreed origins/capabilities interface; request a handoff rather than edit it concurrently. T42 owns first-operator screens, setup-token handling and setup-route transport; it consumes this task's auth-state interface and T30's shared bootstrap service. Neither task creates another auth provider or seed path.

## Required behavior

1. Replace Firebase `User`, listeners, `firebaseUser` and token acquisition with explicit initializing, authenticated, unauthenticated and temporarily unavailable API-session states.
2. Load `/api/me` under T30's authenticated session contract. Preserve organization selection and authorization. Remove Firebase assumptions from guards, profile/sidebar identity, logout and API mocks.
3. Centralize credentialed requests and unsafe-request CSRF headers. Send credentials only to the validated API origin. Never expose/store session secrets, attach credentials to arbitrary URLs or automatically replay unsafe mutations.
4. Implement password sign-in, logout, password change/settings and enabled recovery using T30's flows. Expired/invalid flows have an explicit restart path, safe field validation and sanitized actionable errors.
5. Disable public registration. Show Google/email recovery only when runtime capabilities enable it; ordinary password use requires no Firebase, Google or SMTP configuration.
6. Preserve safe same-dashboard deep links and query strings through login. Reject external/protocol-relative return targets. Preserve the CLI approval route, explicit human consent and permission checks.
7. Clear authenticated/profile state on confirmed expiry or logout. Temporary identity-service failure instead presents a blocking retry state without logging out a valid session, losing the intended return path or causing redirect loops.
8. Expose an explicit setup-required state/capability for T42. Do not implement operator creation, setup-token persistence, membership repair or another bootstrap service. A seeded ready operator reaches the normal application view; a transient profile error must not create a new workspace.
9. Remove Firebase consumers owned here and coordinate runtime/build removal with T41. Preserve unrelated customer examples and historical references; do not retain dual authentication.

## Focused acceptance

Write and run dashboard tests for:

- Startup/authenticated profile loading, confirmed expiry and logout.
- Credential rejection versus dependency outage, retry and absence of redirect loops.
- Password/recovery validation, expired flows and disabled optional capabilities.
- Exact API-origin credential use, CSRF injection and no unsafe-request replay.
- Safe deep links/query strings and rejected external return targets.
- CLI approval return paths, permission failures and preserved explicit consent.
- Disabled public signup, seeded-operator navigation and setup-required handoff.
- No session/setup token in URLs, local storage, rendered diagnostics or errors.

Run affected dashboard tests, typecheck and production build using documented repository commands. Replace obsolete Firebase mocks rather than preserve compatibility code. Report exact results and explicit environment gaps.

Codex owns final real-browser, separate-origin HTTPS cookie/CORS/CSRF and cross-feature integration tests after all required PRs merge. No deployment, paid resources or real operator conversion belongs to this task.

## Local implementation handoff (2026-11-10)

The dashboard now exports the frozen auth states, profile/capabilities and explicit
refresh/logout operations. CSRF is held only in the API transport's memory.
Confirmed 401 clears the profile; temporary dependency failure blocks protected
content while retaining the profile and intended route. Empty membership lists
and profile errors never trigger automatic organization creation.

Ordinary routes are `/sign-in`, `/recovery` and `/settings/account`; public signup
has been removed. Login preserves safe dashboard paths/query strings, including
CLI approval. Recovery verification leads to password settings. CLI approval
remains an explicit owner/admin action. T42 receives the setup-state interface,
not a second provider/bootstrap implementation.

With explicit user authorization, this session also fills the missing T30
start/read/submit proxy routes. It verifies successful authentication with Kratos
`/sessions/whoami`, admits only immutable bound identities, bounds bodies and
upstream requests, rewrites allowlisted cookies/actions and rotates local secrets.
It fixes browser-session claim reconstruction, camel-case middleware errors and
rotation when Kratos retains its unique upstream session ID.

T41 retains exclusive runtime-config/startup/image ownership; its remaining
Firebase configuration fields are unused by this dashboard and must be removed
there. The Firebase SDK, consumers and mocks owned by T31 are removed.

Focused evidence: dashboard typecheck and production build pass; the full
dashboard suite passes 241 tests. The selected .NET
browser-flow/session/authorization/migration/telemetry regressions pass 64 tests,
including the 64 KiB chunked-body limit and 10-second post-header deadline.
Raw output/build assets stay under ignored `.local/artifacts/`.

This is not full task or release qualification. [Issue #44](https://github.com/muniventures/minicloud/issues/44)
tracks missing optional Google provider navigation/callback handling and actual
Kratos/cookie/recovery response-shape qualification. Keep Google disabled until
that work is complete. No real operator, external provider, SMTP service, hosted
deployment or separate-origin browser acceptance was exercised. T90/T91 remain
the final combined/hosted gates. No changes were pushed.

## Copilot assignment and handoff

Implement only T31 after T30 merges. Consume its API contracts, preserve organization authorization and CLI consent, and submit one focused dashboard PR. Hand off the auth-state/capability interface, route changes, focused test/build results and limitations to T42 and the integration owner. Update only this task's status; do not mark final browser integration or hosted acceptance complete from mocked tests.
