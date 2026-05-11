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

## Create `minicloud.yml`

From your app repository:

```bash
minicloud init
```

The wizard lists existing apps in your organization and includes `Create new app`. The generated `minicloud.yml` always includes the selected or newly created `appId`.

For more prompts:

```bash
minicloud init --advanced
```

Use another config path:

```bash
minicloud init --config path/to/minicloud.yml
minicloud deploy --config path/to/minicloud.yml
```

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

Frontend and backend app:

```yaml
app: teamcore
database: postgres
services:
  frontend:
    sourcePath: modules/ui/dashboard
    port: 3000
    public: true
    path: /
    healthPath: /
  backend:
    sourcePath: modules/api
    dockerfile: modules/api/Dockerfile
    port: 8080
    public: true
    path: /api
    healthPath: /health
    env:
      ASPNETCORE_ENVIRONMENT: Staging
```

Custom multi-service app:

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
    path: /api
    healthPath: /health
  worker:
    sourcePath: worker
    port: 8080
    public: false
    path: /worker
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
| `public` | Yes | Whether the service receives public HTTP traffic. At least one service must be public. |
| `path` | Yes | Public route path. Must start with `/`. For private services, still set a stable path value. |
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

On deploy, the CLI requires `appId` in `minicloud.yml`. App selection or app creation happens in `minicloud init`, not during deploy.

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

## Environment Defaults

Show CLI environment defaults:

```bash
minicloud --env
```

Supported environment variables:

| Variable | Description |
| --- | --- |
| `MINICLOUD_TOKEN` | API key used by CLI commands. |
| `MINICLOUD_API_URL` | API base URL. Defaults to `https://cloud.muni.dev/api`. |
| `MINICLOUD_REGISTRY_HOST` | Registry host used for CLI image publishing. |
| `MINICLOUD_REGISTRY_GHCR_OWNER` | Runtime GHCR owner. |
| `MINICLOUD_RUNTIME_REGISTRY_PREFIX` | Runtime image prefix. |
