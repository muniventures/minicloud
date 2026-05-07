# Architecture (AI-optimized)

This folder is the source of truth for Municloud architecture. It exists to help coding agents and humans understand the project quickly without scanning everything.

Municloud is a lightweight deployment system for developers who want managed-platform ergonomics and boring bills without thinking about VPS operations. V0 is the product for the project owner's own use. V1 is the first externally exposed mini-SaaS where Municloud provisions and manages the VPS under the hood, bills the customer for VPS cost plus markup, and exposes a simple GitHub-to-deploy product experience.

## Always read these first

- [agent-instructions.md](agent-instructions.md)
- [backend.md](backend.md)
- [frontend.md](frontend.md)
- [runtime.md](runtime.md)
- [cli.md](cli.md)
- [data-integrations.md](data-integrations.md)
- [provisioning-and-billing.md](provisioning-and-billing.md)
- [security.md](security.md)

## Reference maps

- [agent-lookup.md](agent-lookup.md) maps common task wording to likely files and docs.
- [domain-map.md](domain-map.md) maps product domains to planned components.

## Product and execution plans

- [../product-spec.md](../product-spec.md) is the seed product spec.
- [../product-plan.md](../product-plan.md) is the V0/V1 implementation plan.
- [../../features/FEATURES_REGISTRY.md](../../features/FEATURES_REGISTRY.md) tracks executable feature specs.

## Update policy

If a change affects architecture, command contracts, config schema, deployment flow, runtime layout, security boundaries, secrets, GitHub integration, generated workflows, provider provisioning, pricing, billing, or server filesystem conventions, update the relevant docs here in the same change.

## Planned repo map

V0 begins with GitHub Actions workflows and provider API calls:

- `.github/workflows/municloud-provision.yml` -> create a VPS through a chosen provider API and bootstrap it with cloud-init
- `.github/workflows/municloud-deploy.yml` -> deploy a tagged image to the managed VPS
- `.github/workflows/municloud-logs.yml` -> read Docker Compose logs through a manual workflow
- `.github/workflows/municloud-rollback.yml` -> restore a previous image tag through a manual workflow
- `templates/runtime/` -> Caddy, Compose, systemd, and app directory templates

V1 should introduce productized packages and services:

- `modules/api` -> hosted Municloud control-plane API, Firebase auth, API keys, app/server/deployment registry, provider orchestration, deploy history, and billing foundation
- `docs/architecture/local-registry-testing.md` -> local CLI-to-registry proxy smoke path without GHCR credentials
- `modules/dashboard` -> user-facing React dashboard for onboarding, apps, deploys, API keys, domains, server status, and settings
- `modules/tests` -> backend tests for the control-plane API and service layer
- `modules/cli` -> Municloud CLI
- `modules/runtime` -> server-side runtime scripts or small service
- `modules/shared` -> config schema, release metadata, validation, and shared contracts

## How agents should use this

1. Read [agent-instructions.md](agent-instructions.md) for shared product and workflow guidance.
2. Use [agent-lookup.md](agent-lookup.md) when you need the fastest path from task shape to starting docs or code.
3. Read [backend.md](backend.md) when touching `modules/api`, backend tests, control-plane data, API contracts, auth, provider orchestration, or deployment services.
4. Read [frontend.md](frontend.md) when touching `modules/dashboard`, routes, dashboard API clients, dashboard auth, or UI modules.
5. Read [runtime.md](runtime.md), [cli.md](cli.md), [data-integrations.md](data-integrations.md), [provisioning-and-billing.md](provisioning-and-billing.md), and [security.md](security.md) based on what you will touch.
6. Use [domain-map.md](domain-map.md) to identify the product area and boundaries.
7. If work changes architecture, update these docs.
