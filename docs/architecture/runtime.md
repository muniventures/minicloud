# VPS runtime architecture

This document describes the planned Minicloud server runtime and V0 GitHub Actions behavior.

## Runtime goal

The runtime turns a fresh Ubuntu LTS VPS into a predictable host for Docker-based apps. It should stay understandable to any developer who can SSH into the box and run Docker Compose.

## Core components

- Ubuntu LTS
- Docker Engine and Docker Compose plugin
- Caddy for reverse proxy and automatic HTTPS
- UFW firewall with SSH, HTTP, and HTTPS allowed
- `minicloud` Linux user for deployment operations
- app directories under `/opt/minicloud/apps`
- release metadata stored as JSON
- app environment files stored on the VPS, outside git

## Filesystem layout

```text
/opt/minicloud/
  bin/
  caddy/
    Caddyfile
    apps/
      <app-name>.caddy
  apps/
    <app-name>/
      current/
      releases/
      shared/
        env
      data/
        sqlite/
        postgres/
      compose.yml
      deploys.json
      rollback.json
  logs/
```

For Docker image deployments, `current/` may contain metadata rather than a full code checkout. The image tag is the release artifact.

## V0 workflows

V0 uses GitHub Actions as the operator surface:

- `.github/workflows/minicloud-provision.yml`
- `.github/workflows/minicloud-deploy.yml`
- `.github/workflows/minicloud-logs.yml`
- `.github/workflows/minicloud-rollback.yml`

Workflow steps and remote commands must be idempotent where practical. Re-running provisioning/bootstrap should repair missing packages or config, not duplicate users, firewall rules, or Caddy entries.

## Deploy flow

1. Receive app name, image tag, domain, service port, and health-check config.
2. Write or update `compose.yml`.
3. Pull the target image.
4. Start or update the Compose project.
5. Run configured post-deploy hook or migration command if present.
6. Run health check within the configured timeout.
7. Append a successful deployment record to `deploys.json`.
8. Preserve the previous image tag for rollback.

Failed health checks should mark the deploy as failed and restore the previous release when safe. Rollback should never rebuild.

## Compose conventions

- One Compose project per Minicloud app.
- Compose project name should be stable and derived from the app name.
- Services should be connected to a shared Caddy network when Caddy needs to reach them.
- Host ports should be avoided for app containers unless required.
- Database volumes must be named and must not be deleted by deploy or rollback commands.

## V0 database modes

V0 supports app-selected database mode on the same VPS:

- `sqlite` mounts `/opt/minicloud/apps/<app>/data/sqlite` into the app container and sets `DATABASE_URL=sqlite:////data/sqlite/app.db`.
- `postgres` adds a `postgres:16-alpine` service to the same Compose project, persists data under `/opt/minicloud/apps/<app>/data/postgres`, waits for Postgres health before starting the web service, and sets a Postgres `DATABASE_URL`.

The separate database VPS topology is deferred until same-VPS app/database deployment is stable.

## Caddy conventions

- The root `Caddyfile` imports per-app config fragments.
- Each app fragment maps one or more domains to the app service.
- Caddy reloads must happen only after config validation succeeds.
- The runtime must support domain removal without leaving stale routes.

## Health checks

Health checks should support:

- HTTP path check, default `/health`
- expected status range, default `200..399`
- timeout, default 60 seconds
- interval, default 2 seconds

Health-check failure messages should include URL, timeout, last status, and last response excerpt when safe.

## Logs

V0 logs can proxy `docker compose logs` through a manually dispatched GitHub Actions workflow.

V1 `minicloud logs` should support:

- app selection
- service selection
- tail count
- follow mode
- since timestamp or duration

## Rollback

Rollback should restore a previous image tag and restart the app through Compose. The first supported rollback target is the previous healthy deployment.

Migration rollback is not automatic in V0 or V1. Destructive database migrations remain the application owner's responsibility unless a future spec introduces backup and migration safeguards.

## Runtime upgrade

Runtime upgrades should be explicit and reversible. V1 can begin with versioned runtime files under `/opt/minicloud/bin` and a `minicloud runtime upgrade` command that installs a new version after validation.
