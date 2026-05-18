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

The wizard lists existing apps in your organization and includes `Create new app`. The generated config always includes the selected or newly created `appId`.

`minicloud init` asks for one service name and service folder, then generates one service definition by default. More services can be added later by running `minicloud init` again. If `minicloud.yml` already exists, the CLI writes a service-specific file such as `minicloud.backend.yml`.

If the selected service folder does not contain `Dockerfile`, init prints a warning and still creates the config. `minicloud deploy` requires a Dockerfile and fails until one exists or `services.<name>.dockerfile` points to one.

To add another service to an existing app:

```bash
minicloud add-service
```

Pass the app slug or ID to skip the app picker:

```bash
minicloud add-service teamcore-dev
minicloud add-service --app teamcore-dev --config minicloud.api.yml
```

`add-service` asks for the new service name and folder, writes a service-specific config, and uses the selected app's existing `appId`.

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
| `services` | Yes | Map of service definitions. One to five services. |

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
| `sourcePath` | Required when publishing with CLI | Directory used as the Docker build context. |
| `dockerfile` | No | Dockerfile path. Defaults to Docker's normal lookup in `sourcePath`. |
| `image` | Required with `--no-publish`; optional otherwise | Full image reference. When CLI publishes, omit this or use the configured Minicloud registry host. |
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
minicloud deploy --tag $(git rev-parse --short HEAD)
minicloud deploy --verbose
```

Publish images without creating a deployment:

```bash
minicloud deploy --publish-only
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

## Service Subdomains

Public services get Minicloud subdomains under `app.muni.dev`.

```bash
minicloud domains list --app <app>
minicloud domains add-subdomain --app <app> --service <service> --label <label>
minicloud domains disable --app <app> --hostname <host>
minicloud domains delete --app <app> --hostname <host>
```

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
| `MINICLOUD_REGISTRY_HOST` | Registry host used for CLI image publishing. |
| `MINICLOUD_REGISTRY_GHCR_OWNER` | Runtime GHCR owner. |
| `MINICLOUD_RUNTIME_REGISTRY_PREFIX` | Runtime image prefix. |
