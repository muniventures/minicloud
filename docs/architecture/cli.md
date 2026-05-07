# CLI architecture

This document describes the Municloud CLI. The first implementation lives in
`modules/cli` as a .NET console app.

## CLI goal

The CLI, if introduced, turns selected V0 workflow operations into a local companion for the V1 mini-SaaS:

```bash
municloud init
municloud server bootstrap root@203.0.113.10
municloud app create
municloud domain add dev.example.com
municloud deploy
municloud logs
municloud status
municloud rollback
```

## Responsibilities

The CLI owns:

- `municloud.yml` initialization and validation
- common app detection where safe
- GitHub Actions workflow generation
- Dockerfile/Compose validation
- SSH server bootstrap
- runtime installation and upgrade
- app registration on the server
- secrets commands
- domain commands
- deploy, logs, status, and rollback commands

The CLI should not own:

- long-running build infrastructure in V1
- the primary customer/account database in V1
- provider-specific VPS provisioning in V1
- automatic destructive database migration rollback

## Config file

Default file: `municloud.yml`.

Initial schema:

```yaml
name: my-app
type: compose
server: dev-vps
domains:
  - dev.example.com
healthcheck:
  path: /health
  timeoutSeconds: 60
services:
  web:
    port: 8080
    image: ghcr.io/example/my-app
    env:
      ASPNETCORE_ENVIRONMENT: Production
      FEATURE_FLAG_X: enabled
```

Config validation should fail with actionable messages. Avoid silently guessing dangerous values such as domains, exposed ports, secret names, or privileged commands.

Config supports one to five services. `backend_only`, `frontend_only`, and
`backend_frontend` remain shortcuts for common service shapes. Use `custom` for
arbitrary multi-service apps such as the Municloud control plane itself
(`api`, `dashboard`, and `registry`).

## Commands

### `municloud init`

Creates `municloud.yml` and optionally a generated GitHub Actions workflow. It should detect Dockerfile or Compose files when possible, then ask the user to confirm choices in interactive mode.

### `municloud server bootstrap <ssh-target>`

Escape-hatch command for development or support. V1 customer onboarding should provision and bootstrap servers through the hosted control plane, not require the user to run this command.

### `municloud app create`

Registers an app on the server and writes the initial Compose and Caddy fragments.

### `municloud deploy`

For V1, this may trigger the hosted control plane, which can in turn coordinate the GitHub Actions build/deploy path. A local image-tag deploy can remain available as an escape hatch.

### `municloud logs`

Streams logs through SSH from the runtime.

### `municloud status`

Shows current image tag, health status, domains, last deploy, running containers, and runtime version.

### `municloud rollback`

Rolls back to the previous healthy release unless a specific release is provided.

### `municloud secrets set/list/remove`

Manages app secrets stored on the VPS. `list` must not print secret values.

### `municloud domain add/remove`

Updates app domain config and reloads Caddy after validation.

## Generated files

Generated files should be readable, stable, and clearly marked. They should be safe to commit when they do not contain secrets.

Generated GitHub Actions workflows should:

- run tests when configured
- build the Docker image
- tag images with commit SHA
- push to GHCR
- SSH into the VPS
- run Municloud deploy commands
- surface health-check failures clearly

## V1 implementation choice

V1 is a mini-SaaS, so a hosted control plane is in scope. Keep GitHub Actions as the default builder, but expose a polished control-plane experience for provider-backed provisioning, onboarding, app/server registry, deploy history, status, logs, and billing. The CLI is optional and should be useful without becoming required for first deploy.

## Current implementation

The initial `modules/cli` implementation targets the V1 hosted-control-plane contract:

- reads `MUNICLOUD_TOKEN` first, then a locally stored token
- stores manual tokens through `municloud token set <token>` with restrictive Unix file permissions
- supports `municloud login --token <token>` as the manual login path
- supports `municloud login` browser approval through `/v1/cli-login-sessions`
- supports `municloud init` as a short interactive wizard that writes `municloud.yml`
- supports `municloud init --advanced` for full config customization
- validates `municloud.yml` before API calls
- publishes service images with Docker during `municloud deploy`
- creates deployments with `municloud deploy`
- forwards per-service `env` mappings from `municloud.yml` into deployment workflow `services_json`
- polls deployments until `succeeded`, `failed`, or `canceled`
- exposes `municloud status <deployment-id>`
- exposes `municloud logs <deployment-id> [--source source] [--tail count]`
- exposes `municloud apps list` and `municloud apps inspect <app>`

`MUNICLOUD_API_URL` overrides the API base URL and defaults to
`https://cloud.muni.dev/api`.

`municloud.yml` remains the canonical project config. The deploy command also
discovers `municloudconfig.yml` when present for local convenience, but new
projects should use `municloud.yml`.

By default, `municloud deploy` should build each service image from the
configured `sourcePath`, push it through the environment registry host
(`registry-dev.muni.dev` for dev/staging, `registry.muni.dev` for production),
resolve the resulting Municloud-owned GHCR refs, and create the deployment with
those runtime-pullable refs. Use `--no-publish` only when the configured image
refs have already been built and pushed by another pipeline.

Browser login creates a short-lived login session, opens the dashboard approval
URL, creates a scoped API key after Firebase-authenticated approval, and clears
the raw token after the CLI exchanges it once.
