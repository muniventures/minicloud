# Frontend architecture (React)

This document describes the Municloud dashboard architecture and target frontend conventions. It follows the same documentation pattern as TeamCore, adapted for Municloud's hosted control-plane dashboard.

Municloud dashboard is built with:

- React
- React Router
- TypeScript
- shadcn/ui (Radix) + Tailwind
- Firebase auth
- lucide-react icons

## Apps

### Dashboard UI

**Project:** `modules/dashboard`

**Purpose:** primary Municloud customer dashboard for sign-up, organization setup, API keys, apps, deployments, servers, domains, deployment status, runtime diagnostics, and future billing.

**Expected entrypoints:**

- App shell: `modules/dashboard/app/root.tsx`
- Route table: `modules/dashboard/app/routes.ts`
- Route registry: `modules/dashboard/app/core/routing/dashboardRoutes.ts`
- API client: `modules/dashboard/app/api/apiClient.ts`
- Firebase auth setup: `modules/dashboard/app/core/auth`

**Recommended structure:**

- `app/core/routes/*` -> route components only
- `app/core/routing/*` -> route registry, metadata, nav, breadcrumbs, guards
- `app/core/layout/*` -> application shell, navigation, top bar, auth layout
- `app/core/auth/*` -> Firebase auth provider, token helpers, protected route handling
- `app/core/config/*` -> dashboard runtime config
- `app/core/components/*` -> shared shell components
- `app/api/*` -> API clients and feature endpoint modules
- `app/models/*` -> shared TypeScript models
- `app/modules/*` -> feature modules
- `app/modules/<Module>/components/*` -> presentational components for one module

Route components should render one module entrypoint. Feature logic belongs in the module, not in the route file.

## Routing pattern

Dashboard routes should be defined in one route registry. Each route entry should include:

- route name
- absolute path
- route file or component
- navigation metadata
- breadcrumb metadata
- required auth/permission metadata

Initial route areas:

- `/` -> authenticated overview/dashboard
- `/sign-in` -> Firebase sign-in
- `/onboarding` -> first organization setup
- `/apps` -> app list
- `/apps/:appId` -> app detail
- `/apps/:appId/deployments` -> deployment history
- `/apps/:appId/deployments/:deploymentId` -> deployment detail and diagnostics
- `/api-keys` -> API key management
- `/settings` -> organization/user settings
- `/billing` -> future billing foundation

Route-level authorization should be centralized in layout/guard components. Feature modules should still fail closed for privileged actions.

## API client

The dashboard should use one shared API client boundary. It should own:

- base URL resolution from runtime config
- Firebase ID token injection
- JSON request/response handling
- common error translation
- request cancellation where supported

Automatic headers:

- `Authorization: Bearer <Firebase token>`
- `X-Organization-Id: <selected organization id>` once organization switching exists
- `Accept-Language` if localization is introduced
- `X-Timezone: <browser timezone>`

Feature-specific endpoint modules should live in `app/api/<feature>Api.ts` and reuse the shared API client.

## Authentication and authorization

Firebase is the user authentication provider. The frontend should keep these concepts separate:

- Firebase user session
- Municloud user profile
- selected organization
- organization membership/role
- API key credentials, which are created in the dashboard but used by external automation

Dashboard route guards should require a Firebase session for authenticated areas and should redirect users without an organization to onboarding.

Authorization should fail closed. Hide privileged UI only as a convenience; the backend remains authoritative.

## Feature modules

### Overview

Shows current apps, latest deployments, failed deployments, active servers, and onboarding gaps.

### Apps

Owns app list, app creation, app settings, service definitions, deployment type, default domain, custom domain metadata, and current deployment state.

### Deployments

Owns deployment timeline, state transitions, health checks, runtime diagnostics, logs links, rollback actions, and public URL display.

### API keys

Owns API key creation, one-time secret reveal, scope selection, key list, last-used metadata, and revocation.

### Servers

Owns server plan, provider, location, runtime version, bootstrap status, assigned app/environment, and diagnostic visibility. Server management should stay intentionally minimal for V1.

### Domains

Owns generated default domains, custom domain validation, Cloudflare/DNS status, HTTPS status, and troubleshooting messages.

### Billing

Future module for plan, invoice, payment method, usage/cost reconciliation, provider cost, and markup visibility.

## UI conventions

- Prefer shadcn/ui components over raw HTML.
- Use lucide-react for icons.
- Use cards for repeated records and dashboards, not for every page section.
- Keep operational screens dense and scan-friendly.
- Prefer sheets or dialogs for create/edit flows.
- Use badges for deployment/server/domain status.
- Use tables for deploy history and API key lists.
- Use timeline/status panels for deployment detail.
- Use toasts for short success/failure feedback and inline errors for form validation.

## State management

The baseline dashboard can start with React state and route loaders/actions where appropriate. Introduce shared state only when there is cross-cutting runtime state that benefits from one owner:

- selected organization
- auth/profile state
- long-running deployment polling
- global sheet/dialog runtime

Avoid duplicating polling loops across modules. Deployment status polling should have one shared owner when multiple screens need it.

## Frontend extension rules

- Route components belong in `app/core/routes`.
- Feature logic belongs in `app/modules/<Module>/<Module>Module.tsx`.
- Presentational components belong in `app/modules/<Module>/components`.
- API endpoints go in `app/api/<feature>Api.ts`.
- Shared models go in `app/models`.
- Do not create multiple React components in the same file when refactoring.
- Prefer shadcn/ui components over raw HTML.
- Use `lucide-react` for icons.
- Wrap non-trivial callbacks with `useCallback`.
- Follow the add/edit flow pattern: Sheet or Modal -> Module -> Form.
- Keep feature launcher components thin.
- Do not hard-code secrets, API keys, provider tokens, or internal diagnostic payloads in UI code.

## Localization

Municloud can start English-only. If localization is introduced, use namespaced messages per feature and avoid hard-coded UI strings in module code.

## Where to look when changing frontend architecture

- Dashboard app: `modules/dashboard`
- Route registry: `modules/dashboard/app/core/routing/dashboardRoutes.ts`
- Routes/layouts: `modules/dashboard/app/routes.ts`, `modules/dashboard/app/core/routes/*`, `modules/dashboard/app/core/layout/*`
- Auth: `modules/dashboard/app/core/auth/*`
- API client: `modules/dashboard/app/api/apiClient.ts`
- Feature modules: `modules/dashboard/app/modules/*`
- Control-plane specs: `features/product/municloud-control-plane`
