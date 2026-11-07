# Runtime Templates

V0 currently renders runtime files directly from GitHub Actions. This folder documents the target files generated on the server so they can later be extracted into templates or a runtime package.

## Generated Server Files

- `/etc/caddy/Caddyfile`
- `/opt/minicloud/metadata.env`
- `/opt/minicloud/caddy/apps/<app>.caddy`
- `/opt/minicloud/apps/<app>/compose.yml`
- `/opt/minicloud/apps/<app>/shared/env`
- `/opt/minicloud/apps/<app>/data/sqlite`
- `/opt/minicloud/apps/<app>/data/postgres`
- `/opt/minicloud/apps/<app>/deploys.json`
- `/opt/minicloud/agent/config.json`
- `/opt/minicloud/agent/runtime-token`
- `/opt/minicloud/agent/minicloud-agent`
- `/etc/systemd/system/minicloud-agent.service`

## Provider Labels

Hetzner servers are resolved through labels instead of GitHub-stored IP variables:

```text
managed-by=minicloud
customer=<customer>
app=<app-name>
env=<environment>
owner=minicloud
```

Deploy, logs, and rollback workflows query the provider for exactly one matching server.

## Database Modes

V0 supports same-VPS database modes:

- `sqlite`: mounts `/opt/minicloud/apps/<app>/data/sqlite` into the app container and sets `DATABASE_URL=sqlite:////data/sqlite/app.db`.
- `postgres`: adds a `postgres:16-alpine` service to the app Compose project, stores data under `/opt/minicloud/apps/<app>/data/postgres`, and sets a Postgres `DATABASE_URL` for the app container.
- `none`: does not create a database service, mount database storage, or inject database environment variables. Use this for manual database setup or external services such as Firebase.

A separate database VPS is intentionally deferred until same-VPS app/database deployments are working.

## Multi-Service Apps

V0 can generate one Compose project with multiple app services. Each service has:

- `name`
- `image`
- `port`
- `public`
- `path`
- `health_path`

Public services are routed through one generated `app.muni.dev` hostname. Longer paths are routed first, so `/api` can point to a backend while `/` points to a frontend.

## Runtime Agent

Customer deployments install or update `minicloud-agent` when the deploy workflow receives an `app_id`. The workflow registers the provider server with the control-plane telemetry API, writes the returned runtime token to the VPS, and starts `minicloud-agent.service` so Docker Compose and Caddy logs are pushed over outbound HTTPS.
