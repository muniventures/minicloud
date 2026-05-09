# Backend architecture (.NET)

This document describes the Municloud backend architecture and target service vocabulary. It follows the same documentation pattern as TeamCore, adapted for Municloud's control-plane, deployment, and hosting domains.

## Projects and responsibilities

### `modules/api` (ASP.NET Core Web API)

**Purpose:** hosted Municloud control-plane API for customer onboarding, Firebase-authenticated accounts, API keys, app registration, deployments, VPS/server registry, deploy history, DNS/HTTPS status, runtime communication, and future billing foundation.

**Entry point:** `modules/api/Program.cs`.

**Target stack:**

- .NET 10 ASP.NET Core Web API
- EF Core + PostgreSQL
- Firebase JWT bearer authentication for dashboard users
- Municloud API keys for customer CI/CLI access
- OpenAPI for local development and agent discoverability
- Structured logging and health checks

**Recommended structure:**

- `Auth/` -> Firebase authentication, API key authentication, authorization policies, scope checks
- `Controllers/` -> thin HTTP controllers only
- `Data/` -> EF Core DbContext, entity configuration, migrations
- `Models/` -> EF Core entity models stored by the control plane
- `Dtos/` -> request/response contracts exposed by API controllers
- `Services/` -> business logic boundaries for accounts, apps, deployments, provider provisioning, DNS, runtime registration, secrets metadata, billing foundation, and audit
- `Deployment/` -> deployment orchestration-specific interfaces, state machines, and provider/runtime contracts
- `Validation/` -> reusable request/domain validation helpers
- `Errors/` -> shared error types and exception translation helpers

Controllers should not directly call cloud providers, mutate Caddy/Compose content, or contain deployment orchestration logic. Controllers accept DTOs, resolve the authenticated actor, and delegate to services.

### `modules/tests` (xUnit tests)

**Purpose:** backend unit and integration tests for the control-plane API and service layer.

**Key patterns:**

- Tests should use xUnit.
- EF behavior tests should use PostgreSQL-compatible patterns where possible; SQLite/in-memory tests are acceptable only for logic that does not rely on PostgreSQL-specific behavior.
- Organize tests by layer: `Services/`, `Controllers/`, `Auth/`, `Deployment/`, and `Data/`.
- Service tests should assert tenant scoping, state transitions, audit creation, and failure behavior.

## Control-plane domains

### Accounts and organizations

Municloud uses a simple SaaS account hierarchy:

- `User` represents a Firebase-authenticated human user.
- `Organization` represents a customer account boundary.
- `OrganizationMember` links users to organizations and roles.
- `ApiKey` belongs to an organization and is used by customer CI/CLI integrations.

Every customer-facing app, server, deployment, API key, billing record, audit entry, and provider resource must be organization-scoped.

### Applications

An app is the customer-facing deployable unit. App records own:

- app name and slug
- organization/customer ownership
- - service definitions
- default domain and custom domain metadata
- selected plan
- current deployment pointer

Service definitions are control-plane records that describe image name, port, path, public exposure, and health path. They are not Docker Compose files; Compose is generated at deploy time by the deployment/runtime layer. Apps can define one to five services. 

### Deployments

Deployments are append-only lifecycle records. They should capture:

- requested images/services
- requested commit SHA/ref
- triggering actor (`user`, `api_key`, `github_action`, or future `cli`)
- state transitions
- runtime/server selected for the deployment
- health check results
- public URL
- failure reason and diagnostic summary

Before creating or redispatching a deployment, the API must verify that any
existing generated domain or VPS/server record for the target app belongs to the
same organization/app. Cross-organization reuse of deployment domains or
provider-backed server records must be rejected before dispatch.

Deployment status should move through explicit states such as `queued`, `provisioning`, `deploying`, `verifying`, `healthy`, `failed`, and `rolled_back`.

Do not overwrite historical deployment rows to represent new deployments. Create a new deployment and update the app's current pointer only after verification succeeds.

### Servers and runtime registration

Servers are Municloud-owned VPS instances. The control plane owns the server registry and provider metadata:

- provider (`hetzner`, future `digitalocean`)
- provider server ID
- public IPv4/IPv6
- region/location
- plan/SKU
- bootstrap status
- runtime version
- registration token status
- assigned app ownership

Avoid storing long-lived per-server SSH private keys in the control plane. V1 should prefer one-time bootstrap registration tokens and an outbound runtime token/agent model. SSH may remain a V0 implementation detail behind service interfaces.

### Provider provisioning

Provider provisioning belongs behind service interfaces. Provider services own:

- plan-to-provider/SKU mapping
- server creation
- SSH key/bootstrap material creation or registration token creation
- provider labels/tags
- provider status reads
- teardown eligibility checks

Provider clients must not leak into controllers or dashboard contracts.

### DNS and HTTPS

DNS and HTTPS are separate concerns:

- DNS service owns Cloudflare record creation/update and propagation status.
- Runtime/Caddy service owns Caddy config generation, reload/restart, certificate readiness, and diagnostics.
- Deployment service coordinates both and records deployment-visible status.

Cloudflare DNS records can be DNS-only or proxied depending on feature spec. Current V0 behavior uses DNS-only records so Caddy can issue public certificates directly. Any switch to proxied mode must include an explicit SSL/TLS mode decision and verification path.

### API keys

API keys are customer integration credentials. The backend stores only hashed secrets and displays the raw key exactly once at creation time.

API keys should have:

- organization ownership
- user-created metadata
- prefix for identification
- hashed secret
- scopes
- created/revoked timestamps
- last-used metadata

Recommended scopes:

- `apps:read`
- `apps:write`
- `deployments:read`
- `deployments:write`
- `logs:read`
- `api_keys:manage`

CI/customer deploy calls should use API keys, not Firebase user tokens.

### Audit

Security-relevant actions should write append-only audit records:

- API key creation, use, and revocation
- app create/update/delete
- deployment requested/completed/failed
- server provisioned/assigned/teardown requested
- DNS/custom domain changes
- billing plan changes once billing exists

Do not create multiple audit subsystems. Extend the shared audit service/model.

## Cross-cutting concerns

### Data access

- Primary hosted DB is PostgreSQL via EF Core.
- Use EF migrations for schema changes once the baseline exists.
- Do not modify committed migrations. Create a new migration for schema changes.
- Keep entity models persistence-oriented and keep request/response contracts in DTOs.
- Use explicit organization IDs in queries and commands. Tenant scoping must be enforced in services, not only in controllers.

### Authentication

- Dashboard authentication uses Firebase JWT bearer tokens.
- User identity is Firebase UID.
- API keys authenticate external automation and should resolve to an organization plus scope set.
- Authorization policies should fail closed.

### Deployment orchestration

Deployment orchestration should be service-driven and explicit. The backend should treat deployment as a stateful workflow with persisted state transitions rather than a long controller action.

Initial implementation may call GitHub Actions or SSH-based workflows, but the service boundary should remain stable so later runtime-agent work can replace the transport.

### Secrets

The control plane should not store customer app secrets in plain text. For V1 foundation:

- store secret names and metadata
- push secret values directly into the runtime or a provider-specific secret store when the feature exists
- never log raw secrets
- never return raw secret values after creation/update

### Observability

The API should expose:

- `/health` for container/runtime health
- OpenAPI in development
- structured logs around deployment state changes
- diagnostics summaries for failed deployments

External telemetry should be optional. Missing telemetry configuration must not prevent the API from starting.

## Backend extension rules

- Controllers should stay thin and delegate business logic to services.
- Controller inputs should use DTOs, not EF entities.
- Services are the main business-logic and data-access boundary.
- EF entities should avoid back-reference navigation properties unless there is a strong query need.
- Use curly braces for C# control-flow blocks.
- Use cancellation tokens on async service/data operations.
- Use transactions for multi-entity state transitions.
- When using EF Core execution strategies with transactions, wrap `BeginTransactionAsync()` inside the execution strategy.
- Do not place provider SDK calls directly in controllers.
- Do not place GitHub Actions, SSH, Cloudflare, or VPS-provider details in dashboard-facing DTOs unless the UI explicitly needs them.

## Where to look when changing backend architecture

- API entrypoint: `modules/api/Program.cs`
- Backend tests: `modules/tests`
- Feature specs: `features/product/municloud-control-plane`
- Runtime/deployment docs: `docs/architecture/runtime.md`
- Provider/billing docs: `docs/architecture/provisioning-and-billing.md`
- Data/integration docs: `docs/architecture/data-integrations.md`
- Security docs: `docs/architecture/security.md`
