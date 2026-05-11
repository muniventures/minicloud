# CLI architecture

This document describes the Minicloud CLI. The first implementation lives in
`modules/cli` as a .NET console app.

## CLI goal

The CLI, if introduced, turns selected V0 workflow operations into a local companion for the V1 mini-SaaS:

```bash
minicloud init
minicloud server bootstrap root@203.0.113.10
minicloud app create
minicloud domain add dev.example.com
minicloud deploy
minicloud logs
minicloud status
minicloud rollback
```

## Responsibilities

The CLI owns:

- `minicloud.yml` initialization and validation
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

Default file: `minicloud.yml`.

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

Config supports one to five services. Service names and routes define the deployment shape.

## Commands

### `minicloud init`

Creates `minicloud.yml` and optionally a generated GitHub Actions workflow. It should detect Dockerfile or Compose files when possible, then ask the user to confirm choices in interactive mode.

### `minicloud server bootstrap <ssh-target>`

Escape-hatch command for development or support. V1 customer onboarding should provision and bootstrap servers through the hosted control plane, not require the user to run this command.

### `minicloud app create`

Registers an app on the server and writes the initial Compose and Caddy fragments.

### `minicloud deploy`

For V1, this may trigger the hosted control plane, which can in turn coordinate the GitHub Actions build/deploy path. A local image-tag deploy can remain available as an escape hatch.

### `minicloud logs`

Streams logs through SSH from the runtime.

### `minicloud status`

Shows current image tag, health status, domains, last deploy, running containers, and runtime version.

### `minicloud rollback`

Rolls back to the previous healthy release unless a specific release is provided.

### `minicloud secrets set/list/remove`

Manages app secrets stored on the VPS. `list` must not print secret values.

### `minicloud domain add/remove`

Updates app domain config and reloads Caddy after validation.

## Generated files

Generated files should be readable, stable, and clearly marked. They should be safe to commit when they do not contain secrets.

Generated GitHub Actions workflows should:

- run tests when configured
- build the Docker image
- tag images with commit SHA
- push to GHCR
- SSH into the VPS
- run Minicloud deploy commands
- surface health-check failures clearly

## V1 implementation choice

V1 is a mini-SaaS, so a hosted control plane is in scope. Keep GitHub Actions as the default builder, but expose a polished control-plane experience for provider-backed provisioning, onboarding, app/server registry, deploy history, status, logs, and billing. The CLI is optional and should be useful without becoming required for first deploy.

## Current implementation

The initial `modules/cli` implementation targets the V1 hosted-control-plane contract:

- reads `MINICLOUD_TOKEN` first, then a locally stored token
- stores manual tokens through `minicloud token set <token>` with restrictive Unix file permissions
- supports `minicloud login --token <token>` as the manual login path
- supports `minicloud login` browser approval through `/v1/cli-login-sessions`
- supports `minicloud init` as a short interactive wizard that writes `minicloud.yml`
- supports `minicloud init --advanced` for full config customization
- validates `minicloud.yml` before API calls
- requires `appId` in `minicloud.yml`; `minicloud init` selects an existing app or creates one before writing config
- publishes service images with Docker during `minicloud deploy`
- creates deployments with `minicloud deploy`
- forwards per-service `env` mappings from `minicloud.yml` into deployment workflow `services_json`
- polls deployments until `succeeded`, `failed`, or `canceled`
- exposes `minicloud status <deployment-id>`
- exposes `minicloud logs <deployment-id> [--source source] [--tail count]`
- exposes `minicloud apps list` and `minicloud apps inspect <app>`
- exposes `minicloud --env` for API and registry environment defaults

`MINICLOUD_API_URL` overrides the API base URL and defaults to
`https://cloud.muni.dev/api`.

`minicloud.yml` remains the canonical project config. The deploy command also
discovers `minicloudconfig.yml` when present for local convenience, but new
projects should use `minicloud.yml`.

By default, `minicloud deploy` should build each service image from the
configured `sourcePath`, push it through the registry host
(`registry-dev.muni.dev` for dev/staging, `registry.muni.dev` for production),
resolve the resulting Minicloud-owned GHCR refs, and create the deployment with
those runtime-pullable refs. Use `--no-publish` only when the configured image
refs have already been built and pushed by another pipeline.

Browser login creates a short-lived login session, opens the dashboard approval
URL, creates a scoped API key after Firebase-authenticated approval, and clears
the raw token after the CLI exchanges it once.
