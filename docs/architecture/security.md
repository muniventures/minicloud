# Security architecture

Security is part of the product, not a layer added after deployment works.

## Security goals

- Normal deploys must not require root.
- Server bootstrap may use root, but should leave a restricted runtime behind.
- GitHub workflows should use least-privilege permissions.
- Pull request code must not get privileged deployment credentials by default.
- Secrets must not be committed, printed, or copied into logs.
- Every privileged server action should be inspectable.

## Server users

Bootstrap can connect as `root` or another sudo-capable user.

Runtime deploy operations should use a restricted `minicloud` user:

- owns `/opt/minicloud/apps`
- can run required Docker Compose commands
- can reload Caddy only through a controlled command path
- cannot perform arbitrary root operations during normal deploys

## SSH

Use key-based SSH only. V1 should support generating and installing a deploy key, but must make key paths and permissions explicit.

V0 private keys can live in GitHub Actions secrets for internal use. V1 should avoid long-lived per-server SSH private keys in the hosted control plane unless a later security spec explicitly approves the design. Prefer cloud-init one-time registration tokens and an outbound runtime agent.

## GitHub Actions permissions

Generated workflows should request only the permissions they need, typically:

- `contents: read`
- `packages: write`

Deployment secrets should not be exposed to pull request workflows from forks.

## Registry credentials

Use GitHub-provided tokens where possible for GHCR push. Server-side pulls should use a read-only package token or another minimal credential if needed for private images.

For CLI-first external deployments, prefer `registry-dev.muni.dev` for
dev/staging and `registry.muni.dev` for production as Minicloud upload auth
boundaries backed by Minicloud-owned GHCR. The CLI authenticates to
Minicloud with short-lived push tokens and uploads through the proxy. Runtime
pulls use Minicloud-owned GHCR refs and Minicloud-managed pull credentials.
Minicloud's upstream GHCR credential stays server-side only and must never be
returned to customers, runtime logs, deployment payloads, or dashboard
responses.
The internal deployment workflow must use the Minicloud registry GHCR
credential for manifest validation and runtime `docker login` when deploying
CLI-published images; the `GHCR_READ_TOKEN` environment secret is required.
The workflow uses the GitHub actor as the Docker login username.

## App secrets

App secrets should be stored in server-side environment files owned by the runtime user with restrictive permissions.

`minicloud secrets list` must show names only. `minicloud logs` and health-check errors must avoid printing secret values.

## Control-plane security

V1 is a mini-SaaS and needs baseline SaaS security:

- authenticated user accounts
- organization or workspace boundary
- GitHub App installation ownership checks
- provider account API credentials isolated to Minicloud infrastructure
- server provisioning authorization checks
- server registration tokens
- deploy audit events
- subscription/billing access checks
- rate limiting for public APIs
- encrypted storage for sensitive tokens

Deployment dispatch must enforce ownership of the target infrastructure. If a
generated `app.muni.dev` domain or managed VPS/server record already exists, it
can only be reused by the same organization/app that owns the existing record.

Prefer an outbound runtime agent or short-lived server registration token over storing SSH private keys.

## Provider and abuse controls

Because Minicloud provisions VPSs in Minicloud-owned provider accounts, V1 must include basic abuse controls:

- payment or private-beta approval before provisioning
- plan limits for servers, regions, bandwidth assumptions, and app count
- provider tags that map servers back to customer/workspace IDs
- automated suspend/destroy paths for abuse or unpaid accounts
- admin override tooling
- provider cost monitoring and anomaly alerts

## Command audit

V0 can log deploy metadata to `deploys.json`.

V1 should record:

- actor when known
- app
- provider server ID when relevant
- commit SHA/image tag
- deploy start/end time
- status
- health-check result
- rollback target

## Explicit non-goals for V0

- No multi-tenant hosted secret vault.
- No arbitrary remote command API exposed over the public internet.
- No automatic deployment of untrusted fork pull requests.
- No Kubernetes RBAC or cluster security model.
