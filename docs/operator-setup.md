# First operator setup and provider guidance

This is the browser transport for the [shared host bootstrap](bootstrap.md),
not a second installer, identity registration service or recovery endpoint.
The packaged stack/installer and full-stack acceptance remain separate work;
these source paths do not establish release or hosted qualification.

## Complete an unclaimed installation

1. The host administrator runs migrations, configures the stable
   `Bootstrap:InstallationId`, and uses the shared host `prepare` operation
   with its stable operation ID and protected token output. Keep both IDs
   across restart/upgrade. Never issue tokens from the browser.
2. Open the configured **HTTPS dashboard** at `/setup`. The API must also
   use HTTPS; `Kratos:DashboardOrigin` must exactly equal the dashboard
   origin and be present in `Cors:AllowedOrigins`. Missing Origin, another
   CORS-approved origin, trailing slashes and origin lookalikes cannot
   submit setup. Configure the trusted HTTPS reverse-proxy scheme correctly;
   never disable HTTPS checks to work around proxy configuration.
3. Paste the one-time token into the masked setup field and enter the
   operator email, display name, password and workspace name/slug.
   The password is 8–1024 characters; workspace slugs are 1–63 lowercase
   letters/digits/hyphens, beginning and ending with a letter/digit.
   Submit once. Setup creates only the operator/workspace/owner membership.
4. Choose **Continue to sign in** after success. Setup does not issue a
   browser session. Ordinary password login leads to `/getting-started`.

Never put setup tokens/passwords in URLs, query strings, local/session
storage, shell arguments, screenshots or diagnostics. The screen keeps only
an in-memory draft; it clears token/password on every submission or status check, keeps
non-secret fields while open, and never automatically replays a mutation.
Closing/reloading the screen discards the draft.

## States and retry

- **Checking**: the screen is reading the shared installation state.
- **Required**: supply the protected host-issued token (30-minute lifetime).
- **In progress**: an interrupted operation exists. Retrying the same details
  reconciles only that operation; an uncertain identity outcome requires
  host inspection, not another installation or new identity.
- **Ready**: no claim form is available. Sign in normally; replay cannot
  reset passwords, replace the owner or create another workspace.
- **Blocked**: populated, foreign or inconsistent state needs explicit host
  reconciliation. Do not delete the bootstrap record/database to retry.
- **Temporarily unavailable**: a dependency failure is not evidence that
  setup is required. **Check status** performs a read, never resubmits.

Invalid tokens must be checked against protected host output. Expired tokens
must be rotated by the host using the same installation/operation IDs.
Reused tokens direct the browser to ordinary sign-in. Rotation, explicit
existing-operator binding and password recovery remain host-only shared
bootstrap operations; there is no public token issuance or recovery route.
Setup permits ten POST attempts per installation per minute, with no queue;
a 429 response includes `Retry-After: 60`.

## Provider and plan onboarding

After login, `/getting-started` reads existing `/api/providers` and
`/api/plans` for the selected workspace. It links to the existing
`/plans/accounts` credential/validation screen and `/plans` region/size
mapping screen. Existing permissions and models remain authoritative.
No provider credentials are collected by setup or the guide, and the guide
performs no writes or VPS allocation.

Continue through **Apps → Environment → Services**. Creating/deploying an
app and choosing its placement are explicit subsequent actions and may
allocate billable infrastructure. Existing configured operators can skip
the guide and use `/apps`. `/onboarding` remains exclusively the existing
authenticated organization-creation flow for users without an organization;
it is never installation bootstrap.

## HTTP interface

- `GET /api/account/setup` returns the shared `BootstrapStatusResponse`
  (`state: required | in_progress | ready | blocked`, optional `expiresAt`).
  It accepts no query string, exposes no identity inventory and does not
  issue a token.
- `POST /api/account/setup` requires exact dashboard Origin, HTTPS,
  `application/json`, no query string, and at most 64 KiB including chunked
  bodies. It accepts exactly `setupToken`, `email`, `password`,
  `displayName`, `organizationName`, `organizationSlug` as strings.
  Installation and operation IDs are never accepted from the browser;
  the configured installation ID and shared service supply them.
- Success returns the existing `BootstrapResult`, not an account session.
  Typed shared errors retain their status/code in the normal
  `ErrorResponse` envelope. Invalid forms use `setup_request_invalid`
  (400/413), rejected Origin `csrf_invalid` (403), insecure transport
  `setup_https_required` (400), rate limiting `setup_rate_limited` (429).
  Identity outages return sanitized 503 `auth_unavailable`, never a setup
  transition. Setup responses, including admission errors, are no-store.

The transport calls only `BootstrapService.StatusAsync` and
`CompleteBrowserAsync`. Token verification/expiry, serialization,
reconciliation, seed, replay and host recovery remain in that shared service.
Setup is initial token admission, not ordinary session CSRF authentication;
it grants no session or machine-credential privileges.
