# Shared agent instructions

This document is the shared instruction source for repository-aware coding agents, including GitHub Copilot, Claude Code, Codex, and future Municloud-specific agents.

Read this after [README.md](README.md), then use [agent-lookup.md](agent-lookup.md) and [domain-map.md](domain-map.md) when you need faster retrieval from task wording to architecture or implementation entrypoints. Continue with [backend.md](backend.md), [frontend.md](frontend.md), and [data-integrations.md](data-integrations.md) based on the area you are changing.

## Product state

Municloud has a working V0 GitHub Actions deployment path and is beginning the hosted control-plane implementation. The current source of product truth is:

- `docs/product-spec.md`
- `docs/product-plan.md`
- feature specs under `features/`
- architecture docs under `docs/architecture/`

Current baseline module layout:

- `modules/api` -> hosted .NET 10 control-plane API for Firebase auth, API keys, apps, deployments, provider orchestration, and deployment history
- `modules/dashboard` -> React Router + shadcn dashboard for onboarding, apps, deploys, API keys, domains, and settings
- `modules/tests` -> xUnit tests for the control-plane API and service layer

V0 is the product for the project owner's own consumption: a personal deployment system for one or more apps on a Municloud-managed cheap VPS. GitHub Actions is the V0 operator surface for provider provisioning, deploy, logs, and rollback. It should be solid and pleasant for internal use, but it does not need a CLI, public sign-up, billing, multi-user account management, or a hosted SaaS dashboard.

V1 is the productized version exposed to other developers as a mini-SaaS. Municloud manages VPS provisioning under the hood in Municloud-owned provider accounts, chooses the provider/SKU from product plans, and charges customers the VPS cost plus markup. V1 should include a hosted control plane, GitHub integration, onboarding, accounts, app/server lifecycle, deploy history, logs/status visibility, domains, secrets guidance, rollback, and billing foundation.

## Product principles

- Keep the bill boring.
- Prefer explicit generated infrastructure over hidden magic.
- Use standard Linux, SSH, Docker, Docker Compose, Caddy, GHCR, GitHub Actions, and systemd before inventing custom infrastructure.
- Make the happy path short and the failure path inspectable.
- Failed builds must not change the running app.
- Failed deploys should leave the previous version running whenever possible.
- Rollback must not require rebuilding.
- Secrets must not be committed to git.
- Every generated workflow or server file should be readable enough for a developer to repair manually.

## Working agreement

- Keep changes narrowly scoped to the request.
- Prefer centralizing shared behavior in the existing architectural boundary instead of recreating orchestration per feature. If several areas need the same runtime behavior, first look for a shared service, shared dashboard runtime, or shared API boundary before adding per-feature state machines.
- Follow the architecture docs and update them when behavior or boundaries change.
- Prefer idempotent scripts and commands.
- Do not introduce Kubernetes, cloud-specific orchestration, or high-availability machinery in V0 or V1.
- Do not introduce hosted control-plane complexity into V0.
- For V1, assume a hosted mini-SaaS control plane is in scope unless a later feature spec explicitly removes it.
- Provider-managed VPS provisioning is in scope for V0 and V1. In V0 it can be a GitHub Actions workflow with one provider token. In V1 it belongs in the control plane.
- Keep temporary scripts under `scripts/temp/` and delete them when no longer needed.
- When adding executable behavior, include verification steps that can run locally or against a disposable VPS.
- Do not add backward-compatibility layers, migration bridges, dual-read, or dual-write behavior unless the task explicitly asks for them.

## Specs and planning

- Use `features/SPEC_TEMPLATE.md` for new feature specs and major rewrites.
- Feature specs must be grouped by primary area under `features/<area>/<feature>/`.
- New feature folders should normally contain:
  - `1.<feature-name>.md` as the overview spec
  - `0.execution-plan.md` as the live execution tracker
  - `subspecs/*.md` for executable task specs
- Use stable task IDs (`T00`, `T10`, `T20`, etc.).
- Multi-agent execution plans must include explicit dependencies, parallelization notes, and phase plans.
- Keep status current in `0.execution-plan.md` while work is in progress.
- After performing any work, update both `features/FEATURE_BACKLOG.md` and `features/FEATURES_REGISTRY.md` in the same change so the backlog and registry reflect what was done and the current feature status. For feature-spec work, also update the relevant `0.execution-plan.md` and sub-spec status.

## V0 implementation stance

V0 should prove the deployment loop before creating a polished product surface. Prefer:

- GitHub Actions workflows as the primary operator interface
- one provider API integration
- provider token stored in GitHub Actions secrets
- cloud-init/user-data for server bootstrap
- a documented server filesystem layout
- generated but inspectable GitHub Actions workflows
- GHCR image tags by commit SHA
- a server-side `.env` file outside git
- Caddy for HTTPS
- Docker Compose for runtime orchestration
- JSON release metadata for deploy history

V0 should not require:

- a SaaS backend
- a CLI
- a database
- a web dashboard
- multi-server support
- preview environments
- framework-specific build systems beyond Dockerfile or Compose

## V1 implementation stance

V1 should extract the repeated V0 steps into a public product: managed VPS provisioning, small VPS runtime, hosted web app/API, GitHub integration, deploy history, pricing, billing, and account foundation. A CLI may exist as a companion later, but V1 should not depend on users creating or bootstrapping their own VPS.

Prefer a hybrid architecture:

- GitHub Actions builds images and pushes to GHCR.
- The Municloud control plane owns onboarding, GitHub App installation state, provider provisioning, app/server metadata, deploy history, billing, and user-facing status.
- The VPS runtime performs deployments and reports status.
- Any CLI is an escape hatch, not the primary V1 onboarding path.

Avoid storing long-lived per-server SSH private keys in the control plane. Prefer cloud-init one-time registration tokens and an outbound runtime agent/token model for V1.

If introduced, the CLI should own:

- config initialization
- app detection and validation
- workflow generation
- SSH bootstrap
- app registration
- secrets commands
- domain commands
- deploy, logs, status, and rollback commands

The control plane should own:

- user accounts and organizations
- GitHub App installation metadata
- VPS provider selection and provisioning
- server plan/SKU mapping
- server and app registry
- deploy history and status
- web dashboard/API
- billing/subscription foundation
- cost reconciliation and markup metadata
- audit log foundation

The server runtime should own:

- app directory layout
- Compose project management
- Caddy config fragments
- release metadata
- health checks
- log access
- runtime upgrades

## Naming and vocabulary

- Product name: Municloud.
- Config file: `municloud.yml`.
- Server runtime user: `municloud` unless a spec chooses `deploy`.
- App root: `/opt/municloud/apps/<app-name>`.
- Shared runtime root: `/opt/municloud`.
- Default reverse proxy: Caddy.
- Default registry: GitHub Container Registry.
- Default build orchestrator: GitHub Actions.

## Shared coding conventions

- Use the latest repo-supported versions of .NET, React, TypeScript, shadcn/ui, and Entity Framework Core already established in the codebase.
- Use curly braces for C# control-flow blocks.
- Keep comments sparse and only where they clarify non-obvious logic.
- When changing backend models, update the corresponding DbContext/configuration and related DTO/service mappings as needed.
- Keep durable architecture rules in `docs/architecture/`; keep `.github/instructions` files as thin pointers.

## Backend rules

See [backend.md](backend.md) for the complete structure and boundaries. The highest-priority implementation rules are:

- `modules/api` is the hosted Municloud control-plane API.
- Controllers should stay thin and delegate business logic to services.
- Controller inputs must use DTOs, not EF entities.
- Services are the main business-logic, orchestration, and data-access boundary.
- Every customer-facing app, deployment, API key, server, domain, billing record, and audit record must be organization-scoped.
- API keys are external automation credentials and must be stored only as hashed secrets.
- Firebase user tokens authenticate dashboard users; API keys authenticate customer CI/CLI automation.
- Do not place provider SDK calls, Cloudflare calls, SSH calls, or GitHub Actions details directly in controllers.
- Do not modify existing committed EF migrations; create a new migration for schema changes once the baseline exists.
- EF Core transactions must use an execution strategy wrapper before `BeginTransactionAsync()`.

```csharp
var executionStrategy = _context.Database.CreateExecutionStrategy();
await executionStrategy.ExecuteAsync(async () =>
{
    await using var transaction = await _context.Database.BeginTransactionAsync();
    try
    {
        await _context.SaveChangesAsync();
        await transaction.CommitAsync();
    }
    catch
    {
        await transaction.RollbackAsync();
        throw;
    }
});
```

## Frontend rules

See [frontend.md](frontend.md) for dashboard structure and routing. The highest-priority implementation rules are:

- `modules/dashboard` is the hosted Municloud customer dashboard.
- Route components belong in `app/core/routes`.
- Feature logic belongs in `app/modules/<Module>/<Module>Module.tsx`.
- Presentational components belong in `app/modules/<Module>/components`.
- API endpoints go in `app/api/<feature>Api.ts` and reuse the shared API client.
- Prefer shadcn/ui components over raw HTML.
- Use `lucide-react` for icons.
- Wrap non-trivial callbacks with `useCallback`.
- Follow the add/edit flow pattern: Sheet or Modal -> Module -> Form.
- Keep feature launcher components thin.
- Avoid duplicating deployment polling loops across modules; use one shared runtime owner when multiple screens need deployment status.
- Do not hard-code secrets, API keys, provider tokens, or internal diagnostic payloads in UI code.

## Agent response preferences

- Keep implementation and explanations within the scope of the request.
- Do not create code, tests, guides, or examples outside the requested scope unless they are necessary to make the requested artifact usable.
- Ask follow-up questions only when missing information blocks correct implementation.
- When asked for completion status, verify both docs and concrete implementation evidence.
