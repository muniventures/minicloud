# Agent lookup

Use this file when a request names a task but not the right doc or implementation area.

## Bootstrap server

Read:

- `docs/architecture/runtime.md`
- `docs/architecture/provisioning-and-billing.md`
- `docs/architecture/security.md`
- `features/platform/v0-personal-deployment/`

Likely files:

- `.github/workflows/municloud-provision.yml`
- `templates/runtime/`

## Provision VPS or billing

Read:

- `docs/architecture/backend.md`
- `docs/architecture/provisioning-and-billing.md`
- `docs/architecture/security.md`
- `docs/architecture/data-integrations.md`

Likely files:

- `.github/workflows/municloud-provision.yml`
- `modules/api`
- provider adapter code

## Deploy app

Read:

- `docs/architecture/backend.md` if the hosted control plane is involved
- `docs/architecture/runtime.md`
- `docs/architecture/data-integrations.md`

Likely files:

- `.github/workflows/municloud-deploy.yml`
- `modules/api`
- `templates/github-actions/municloud-deploy.yml`
- `templates/github-actions/deploy.yml`
- `municloud.yml`

## Logs or status

Read:

- `docs/architecture/backend.md` if the hosted control plane is involved
- `docs/architecture/frontend.md` if dashboard status surfaces are involved
- `docs/architecture/runtime.md`
- `docs/architecture/cli.md`

Likely files:

- `.github/workflows/municloud-logs.yml`
- `templates/github-actions/municloud-logs.yml`
- `modules/api`
- `modules/dashboard`
- `modules/cli`
- `modules/runtime`

## API, auth, or API keys

Read:

- `docs/architecture/backend.md`
- `docs/architecture/security.md`
- `docs/architecture/data-integrations.md`
- `features/product/municloud-control-plane/`

Likely files:

- `modules/api`
- `modules/tests`

## Dashboard

Read:

- `docs/architecture/frontend.md`
- `docs/architecture/backend.md` if changing API contracts
- `features/product/municloud-control-plane/`

Likely files:

- `modules/dashboard`
- `modules/api`

## Rollback

Read:

- `docs/architecture/runtime.md`
- `docs/architecture/security.md`

Likely files:

- `.github/workflows/municloud-rollback.yml`
- `templates/github-actions/municloud-rollback.yml`
- runtime release metadata code

## CLI command or config

Read:

- `docs/architecture/cli.md`
- `docs/architecture/data-integrations.md`

Likely files:

- `modules/cli`
- `modules/shared`
- `templates/`

## GitHub Actions or GHCR

Read:

- `docs/architecture/data-integrations.md`
- `docs/architecture/security.md`

Likely files:

- `templates/github-actions/deploy.yml`
- workflow generator

## Secrets, SSH, or permissions

Read:

- `docs/architecture/security.md`
- `docs/architecture/runtime.md`

Likely files:

- generated workflow
- provider provisioning code
- runtime registration code

## Product planning

Read:

- `docs/product-spec.md`
- `docs/product-plan.md`
- `features/FEATURES_REGISTRY.md`
- relevant `features/**/0.execution-plan.md`
