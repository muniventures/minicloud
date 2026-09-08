# CLI Documentation

The current command is `minicloud`.

## Authenticate

Create an API key in the Minicloud console with these scopes:

- `apps:read`
- `apps:write`
- `deployments:create`
- `deployments:read`

Then log in:

```bash
minicloud login --token <MINICLOUD_API_KEY>
```

Or store the token directly:

```bash
minicloud token set <MINICLOUD_API_KEY>
```

For CI or temporary shell use:

```bash
export MINICLOUD_TOKEN=<MINICLOUD_API_KEY>
```

## Create A Deployment Config

From your app repository:

```bash
minicloud init
```

The wizard first lists existing apps in your organization and includes `Create new app`. The generated config always includes the selected or newly created `appId`.

Next, `minicloud init` scans up to five folder levels for web services, excluding generated dependency/build folders such as `node_modules`, `.git`, `.next`, `bin`, `obj`, `target`, `dist`, `build`, and `out`. It detects common Node web frameworks, .NET web projects, Spring Boot web projects, and Dockerfile-backed service folders. Non-web .NET projects and Android projects are ignored. Detected services are shown in a multi-select list; use Space to select one or more services and Enter to save. The final `Custom` row is selected with Enter and lets you provide a service name, path, and Dockerfile path manually.

If a selected service folder does not contain `Dockerfile` and no custom Dockerfile path is configured, init still creates the config. On deploy, the CLI generates a Dockerfile for recognized app types such as Vite, Next.js, Node API, .NET web, and Spring Boot services before validation and artifact upload. Unsupported project types still need a checked-in Dockerfile or `services.<name>.dockerfile`.

Public Vite services must allow the generated Minicloud host or `.app.muni.dev` in `preview.allowedHosts`. Public Next.js services must run a production server such as `next start`, not `next dev`, from Dockerfile CMD/ENTRYPOINT.

To add another service to an existing app:

```bash
minicloud add-service
```

Pass the app slug or ID to skip the app picker:

```bash
minicloud add-service teamcore-dev
minicloud add-service --app teamcore-dev --config minicloud.api.yml
```

`add-service` uses the same service detection and custom-service flow, writes a config for the selected services, and uses the selected app's existing `appId`.

For more prompts:

```bash
minicloud init --advanced
minicloud add-service --advanced
```

Use another config path:

```bash
minicloud init --config path/to/minicloud.yml
minicloud deploy --config path/to/minicloud.yml
```

## Managed VPS Plans

The CLI deploys to the app identified by `appId` in `minicloud.yml`. The app's managed VPS plan is selected in the Minicloud control plane.

Current plan codes:

| Plan | Provider | Region | Provider SKU | Provider cost basis |
| --- | --- | --- | --- | --- |
| `P0-H-0` | Hetzner | `nbg1` | `cx23` | Original Hetzner plan |
| `P0-V-0` | Vultr | `ewr` | `vc2-1c-0.5gb-v6` | $2.50/month, IPv6-only |
| `P0-V-1` | Vultr | `ewr` | `vc2-1c-0.5gb` | $3.50/month |
| `P0-V-2` | Vultr | `ewr` | `vc2-1c-1gb` | $5.00/month |
| `P0-D-0` | DigitalOcean | `nyc1` | `s-1vcpu-512mb-10gb` | $4.00/month |
| `P0-D-1` | DigitalOcean | `nyc1` | `s-1vcpu-1gb` | $6.00/month |
| `P0-D-2` | DigitalOcean | `nyc1` | `s-1vcpu-2gb` | $12.00/month |

## Branch Deployments

Deploy every configured service from the currently checked-out Git branch:

```bash
minicloud deploy branch
```

Minicloud creates or reuses a child environment under the configured main app.
Each branch environment has its own `P0-V-0` Vultr VPS, runtime, database,
deployments, and domains. The main app's secrets are copied when the branch is
first created. A branch such as `feature/cart` is normalized to `feature-cart`;
its public service hostname ends in `-feature-cart.app.muni.dev`.

Destroy a branch environment:

```bash
minicloud branch destroy
```

When several branches exist, the CLI shows a selection list. Destruction always
requires confirmation. You can select directly with
`minicloud branch destroy feature-cart`.

## `minicloud.yml` Reference

Minimal backend app:

```yaml
app: my-api
appId: app_123
database: postgres
services:
  backend:
    sourcePath: .
    dockerfile: Dockerfile
    port: 8080
    public: true
    path: /
    healthPath: /health
```

Independent frontend and backend configs can point at the same app:

```yaml
# minicloud.frontend.yml
app: teamcore
appId: app_123
database: postgres
services:
  frontend:
    sourcePath: modules/ui/dashboard
    port: 3000
    public: true
    path: /
    healthPath: /
```

```yaml
# minicloud.backend.yml
app: teamcore
appId: app_123
database: postgres
services:
  backend:
    sourcePath: modules/api
    dockerfile: modules/api/Dockerfile
    port: 8080
    public: true
    path: /
    healthPath: /health
    env:
      ASPNETCORE_ENVIRONMENT: Staging
```

Deploy each service independently:

```bash
minicloud deploy backend --config minicloud.yml
minicloud deploy --config minicloud.backend.yml
minicloud deploy --config minicloud.frontend.yml
```

Custom multi-service deployment file:

```yaml
app: my-platform
database: postgres
services:
  dashboard:
    sourcePath: dashboard
    port: 3000
    public: true
    path: /
    healthPath: /
  api:
    sourcePath: api
    dockerfile: api/Dockerfile
    port: 8080
    public: true
    path: /
    healthPath: /health
  worker:
    sourcePath: worker
    port: 8080
    public: false
    path: /
    healthPath: /health
```

Prebuilt image deployment:

```yaml
app: my-api
database: sqlite
services:
  backend:
    image: ghcr.io/acme/my-api/backend:abc123
    port: 8080
    public: true
    path: /
    healthPath: /health
```

Deploy prebuilt images with:

```bash
minicloud deploy --no-publish
```

## Root Fields

| Field | Required | Description |
| --- | --- | --- |
| `app` | Yes | Stable app slug. Use lowercase letters, numbers, dashes, or underscores. |
| `appId` | Yes | Stable Minicloud app ID. `minicloud deploy` requires this value and sends it with the deployment. |
| `database` | No | `sqlite` or `postgres`. |
| `commitSha` | No | Optional source revision to attach to the deployment. |
| `services` | Yes | Map of service definitions. One to ten services. |

One config file targets one app. Multiple config files can share the same `appId`.

## Database Modes

`sqlite` stores data on the managed runtime disk. Use it for simple apps and low operational overhead.

`postgres` adds a managed Postgres service next to your app. Pass a password on first deploy:

```bash
minicloud deploy --pgpassword '<strong-password>'
```

## Service Fields

| Field | Required | Description |
| --- | --- | --- |
| `sourcePath` | Required for normal CLI deploys | Directory bundled into the deployment artifact and used as the workflow Docker build context. |
| `dockerfile` | No | Dockerfile path. Defaults to Docker's normal lookup in `sourcePath`. |
| `image` | Required with `--no-publish`; optional otherwise | Full image reference for prebuilt-image deploys. Normal deploys omit this and upload a source artifact instead. |
| `port` | Yes | Internal container port, `1` to `65535`. |
| `public` | Yes | Whether the service receives public HTTP traffic. The merged active app must have at least one public service. |
| `path` | Yes | Public route path. First-pass service subdomains require `/`. For private services, still set `/`. |
| `healthPath` | Yes | HTTP health check path. Must start with `/`. |
| `env` | No | String key/value environment variables for the service. |

Service names must use lowercase letters, numbers, dashes, or underscores.

Environment variable names must match:

```text
^[A-Za-z_][A-Za-z0-9_]*$
```

## Runtime Environment Variables And Secrets

Use `services.<name>.env` in `minicloud.yml` to set runtime environment variables for one service. `env` is a YAML mapping whose keys are environment variable names and whose values are strings:

```yaml
services:
  backend:
    sourcePath: .
    dockerfile: Dockerfile
    port: 8080
    public: true
    path: /
    healthPath: /health
    env:
      ASPNETCORE_ENVIRONMENT: Production
      FEATURE_FLAG_X: enabled
      API_BASE_URL: https://api.example.com
```

For multi-service apps, each service gets only its own `env` values:

```yaml
services:
  api:
    sourcePath: api
    port: 8080
    public: true
    path: /
    healthPath: /health
    env:
      ASPNETCORE_ENVIRONMENT: Production
      WORKER_QUEUE: default
  worker:
    sourcePath: worker
    port: 8080
    public: false
    path: /
    healthPath: /health
    env:
      WORKER_QUEUE: default
      JOB_CONCURRENCY: "4"
```

Environment variable names must match `^[A-Za-z_][A-Za-z0-9_]*$`. Values must be strings. Quote values such as numbers, booleans, empty strings, or values with special YAML characters when you need them to stay strings:

```yaml
env:
  JOB_CONCURRENCY: "4"
  FEATURE_ENABLED: "true"
  EMPTY_VALUE: ""
  CALLBACK_URL: "https://example.com/callback?source=minicloud"
```

During `minicloud deploy`, the CLI sends `services.<name>.env` to the Minicloud API with the deployment request. Minicloud stores those values with the deployment service record and forwards them to the deployment workflow as `services_json`. The runtime writes them into the generated Docker Compose `environment:` block for that service.

Do not commit sensitive values in `minicloud.yml`. Use `env` for non-sensitive runtime configuration only.

Use `secretEnv` when a service needs sensitive runtime environment variables. `secretEnv` maps the environment variable name that the container receives to a stored Minicloud secret name:

```yaml
services:
  backend:
    sourcePath: .
    dockerfile: Dockerfile
    port: 8080
    public: true
    path: /
    healthPath: /health
    secretEnv:
      MAPBOX_ACCESS_TOKEN: MAPBOX_ACCESS_TOKEN
      STRIPE_SECRET_KEY: STRIPE_SECRET_KEY
```

Set those stored secrets before deploying. Without `--service`, the CLI creates an app-scoped secret that any service can reference by name:

```bash
minicloud secrets set --app my-api MAPBOX_ACCESS_TOKEN
minicloud secrets list --app my-api
```

Use `--service` to create a service-scoped secret. Service-scoped secrets are only available to the matching service:

```bash
minicloud secrets set --app my-api --service backend STRIPE_SECRET_KEY --value "$STRIPE_SECRET_KEY"
minicloud secrets list --app my-api --service backend
minicloud secrets remove --app my-api --service backend STRIPE_SECRET_KEY
```

If you keep local secret values in `minicloud.secrets.env`, use dotenv-style `NAME=value` lines. Do not commit this file:

```dotenv
# minicloud.secrets.env
MAPBOX_ACCESS_TOKEN=pk_live_example
STRIPE_SECRET_KEY=sk_live_example
OPENAI_API_KEY=sk-proj-example
```

Then load it in your shell and write the values to Minicloud:

```bash
set -a
. ./minicloud.secrets.env
set +a

minicloud secrets set --app my-api MAPBOX_ACCESS_TOKEN --value "$MAPBOX_ACCESS_TOKEN"
minicloud secrets set --app my-api --service backend STRIPE_SECRET_KEY --value "$STRIPE_SECRET_KEY"
minicloud secrets set --app my-api --service worker OPENAI_API_KEY --value "$OPENAI_API_KEY"
```

`minicloud.secrets.env` is a local helper file. During `minicloud deploy`, if the selected service's `sourcePath` contains this file, the CLI reads it and saves each `NAME=value` pair as a service-scoped Minicloud secret before creating the deployment. You can also store values manually with `minicloud secrets set`.

Prefer the interactive prompt. `--value` is intended for controlled CI scripts; values passed on a command line can be captured by shell history or process listings.

The left side of each `secretEnv` entry is the environment variable that the container receives. The right side is the stored secret name to read. Both must match `^[A-Za-z_][A-Za-z0-9_]*$`. `env` and `secretEnv` cannot define the same runtime environment variable.

Secret values do not belong in a deploy config. Keep `secretEnv` references in the selected deploy config, normally `minicloud.yml`, then write the actual values with `minicloud secrets set`.

Minicloud stores secret values in its OpenBao-backed secret store and stores only secret metadata in the control-plane database. During deployment:

- App-scoped secrets can be used by any service that references them in `secretEnv`.
- Service-scoped secrets can be used only by the matching service.
- If an app-scoped secret and a service-scoped secret have the same name, an explicit `secretEnv` reference uses the app-scoped secret.
- If a service has no `secretEnv` mapping, Minicloud injects all service-scoped secrets whose service name matches the deployed service, using each stored secret name as the container environment variable name.

App-scoped secrets are never injected automatically. Secret values are not returned by `minicloud secrets list`.

The reusable GitHub Actions workflow can pass `secretEnv` references, but it never accepts runtime secret values. Create or update secrets through the CLI or portal before deployment. See [GitHub Actions workflow documentation](github-actions.md#runtime-secrets).

Postgres passwords are handled separately. For Postgres deployments, pass the password with:

```bash
minicloud deploy --pgpassword '<strong-password>'
```

Minicloud stores that app-level Postgres password encrypted and reuses it on later deploys.

## Deploy

Build, publish, create deployment, and wait:

```bash
minicloud deploy
```

On deploy, the CLI requires `appId` in the selected config. App selection or app creation happens in `minicloud init`, not during deploy.

Deploy is service-scoped. The CLI publishes and sends only the selected services. Existing services for the same app that are omitted from the selected deployment remain active. Omission does not delete a service.

For a config with multiple services:

```bash
minicloud deploy backend
minicloud deploy frontend backend
minicloud deploy --all
```

If a config has multiple services and no service name or `--all` is provided, the CLI asks which services to deploy. Use Up/Down to move, Space to toggle a service, or toggle `all` to select every service.

Common overrides:

```bash
minicloud deploy --database postgres
```

Deploy already-published images:

```bash
minicloud deploy --no-publish
```

## Status, Logs, And Apps

```bash
minicloud status
minicloud status <deployment-id>
minicloud logs <app-or-deployment-id>
minicloud apps list
minicloud apps inspect <app>
```

`apps inspect` prints the latest deployment and the active service inventory,
including public/private state, port, runtime state, and assigned domains.

## Service Subdomains

Public services get Minicloud subdomains under `app.muni.dev`.

```bash
minicloud domains list --app <app>
minicloud domains add-subdomain --app <app> --service <service> --label <label>
minicloud domains disable --app <app> --hostname <host>
minicloud domains delete --app <app> --hostname <host>
```

`domains list` includes each hostname's service, status, runtime apply state,
TLS state, last applied timestamp, and last updated timestamp.

## Environment Defaults

Show CLI environment defaults:

```bash
minicloud --env
```

Supported environment variables:

| Variable | Description |
| --- | --- |
| `MINICLOUD_TOKEN` | API key used by CLI commands. |
| `MINICLOUD_API_URL` | API base URL. Defaults to `https://api.cloud.muni.dev`. |

### Dockerfile port detection

`init` and `add-service` read each service's Dockerfile independently and use its final stage's literal TCP `EXPOSE` ports instead of framework defaults. Named local base-stage exposures are inherited; unrelated build-stage ports are ignored. Multiple Node services retain their individual Dockerfile ports; the CLI does not renumber container ports. When multiple TCP ports are exposed, the wizard asks which port to route. Advanced and custom service flows also use these values. Without an exposed TCP port, existing framework defaults remain; add a matching `EXPOSE` before deployment. Variable-based or invalid port declarations require an explicit literal port. Deployment validation uses the same final-stage TCP port reader.

- Artifact Dockerfile paths are recorded relative to the archive root, including external Dockerfiles copied into `minicloud-artifact/Dockerfile`. This fixes builds whose Dockerfile lives outside the service source context.

Deploy completion and status output list every assigned service URL with its service name. App inspection and branch output also list all assigned URLs; no URL is treated as the main website.

- Private services: init/add-service explicitly ask whether each service should receive a public URL. Set `public: false` for internal APIs and workers; private containers have no host-port mapping and remain accessible to peer containers by service name. Making a service private disables its existing domain bindings and requests routing reconciliation; deployment responses omit private-service URLs. Workers may retain internal health listeners without public URLs. Portless worker processes remain a separate capability.
