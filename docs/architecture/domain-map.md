# Domain map

This file maps Municloud product domains to planned docs, features, and code areas.

## Deployment runtime

Owns server bootstrap, app directories, Compose, Caddy, health checks, deploy history, logs, rollback, and runtime upgrades.

Docs:

- `docs/architecture/runtime.md`
- `features/platform/v0-personal-deployment/`

Planned code:

- `.github/workflows/municloud-provision.yml`
- `.github/workflows/municloud-deploy.yml`
- `.github/workflows/municloud-logs.yml`
- `.github/workflows/municloud-rollback.yml`
- `templates/github-actions/`
- `modules/runtime`

## Provisioning and billing

Owns provider selection, server creation, cloud-init bootstrap, server lifecycle, pricing, markup, subscription state, provider cost reconciliation, suspension, and teardown.

Docs:

- `docs/architecture/backend.md`
- `docs/architecture/provisioning-and-billing.md`
- `docs/architecture/security.md`
- `features/platform/v0-personal-deployment/`
- `features/product/v1-productized-mvp/`
- `features/product/municloud-control-plane/`

Planned code:

- `.github/workflows/municloud-provision.yml` for V0
- `modules/api`
- `modules/shared`

## CLI

Owns command UX, config validation, workflow generation, SSH orchestration, and user-facing status/errors.

Docs:

- `docs/architecture/cli.md`
- `features/product/v1-productized-mvp/`

Planned code:

- `modules/cli`
- `modules/shared`

## GitHub integration

Owns GitHub App installation, generated or managed workflow, CI/CD shape, GHCR image tags, GitHub secrets expectations, webhook handling, and deploy visibility.

Docs:

- `docs/architecture/data-integrations.md`
- `docs/architecture/security.md`

Planned code:

- `.github/workflows/municloud-deploy.yml`
- `.github/workflows/municloud-logs.yml`
- `.github/workflows/municloud-rollback.yml`
- CLI workflow generator

## Domains and HTTPS

Owns domain commands, Caddy fragments, certificate readiness, DNS validation, and reverse proxy config.

Docs:

- `docs/architecture/backend.md`
- `docs/architecture/runtime.md`
- `docs/architecture/cli.md`

Planned code:

- `modules/api`
- `modules/dashboard`

## Secrets

Owns GitHub Actions secret expectations and app runtime environment files.

Docs:

- `docs/architecture/security.md`
- `docs/architecture/data-integrations.md`

## Control plane

In scope for V1. The control plane owns user accounts, provider provisioning, plan/SKU mapping, app/server registry, GitHub App webhooks, centralized deploy history, status, notifications foundation, billing foundation, and dashboard/API surfaces.

Docs:

- `docs/architecture/backend.md`
- `docs/architecture/frontend.md`
- `docs/architecture/data-integrations.md`
- `docs/architecture/provisioning-and-billing.md`
- `docs/architecture/security.md`
- `features/product/municloud-control-plane/`

Planned code:

- `modules/api`
- `modules/dashboard`
- `modules/tests`
