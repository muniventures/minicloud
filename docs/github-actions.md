# GitHub Actions Workflow

Use the reusable Minicloud workflow when you want GitHub Actions to build images, push them to GHCR, and ask the Minicloud API to start a deployment.

## Prerequisites

Create a Minicloud API key in the console with these scopes:

- `deployments:create`
- `deployments:read`

Add these secrets to your app repository. Repository or organization secrets work.

| Secret | Required | Description |
| --- | --- | --- |
| `MUNICLOUD_API_KEY` | Yes | Minicloud API key from the console. |
| `MUNICLOUD_POSTGRES_PASSWORD` | Only for `database: postgres` | Postgres password used by the deployment. |

Add this repository or organization variable:

| Variable | Required | Description |
| --- | --- | --- |
| `MUNICLOUD_APP_ID` | Yes | App id from the Municloud console URL, for example `app_52kqllu8k1nq4kg352kqllu8`. |

Set workflow permissions:

```yaml
permissions:
  contents: read
  packages: write
```

## Backend App

Create `.github/workflows/minicloud-deploy.yml`:

```yaml
name: Deploy With Minicloud

on:
  push:
    branches:
      - main
  workflow_dispatch:

permissions:
  contents: read
  packages: write

jobs:
  minicloud:
    uses: muniventures/minicloud/.github/workflows/customer-deploy.yml@main
    with:
      app_id: ${{ vars.MUNICLOUD_APP_ID }}
      database: postgres
      services: |
        - name: backend
          sourcePath: .
          dockerfile: Dockerfile
          port: 8080
          public: true
          path: /
          healthPath: /health
    secrets:
      municloud_api_key: ${{ secrets.MUNICLOUD_API_KEY }}
      postgres_password: ${{ secrets.MUNICLOUD_POSTGRES_PASSWORD }}
```

## Frontend And Backend App

```yaml
name: Deploy With Minicloud

on:
  push:
    branches:
      - main
  workflow_dispatch:

permissions:
  contents: read
  packages: write

jobs:
  minicloud:
    uses: muniventures/minicloud/.github/workflows/customer-deploy.yml@main
    with:
      app_id: ${{ vars.MUNICLOUD_APP_ID }}
      database: postgres
      services: |
        - name: frontend
          sourcePath: ./modules/ui/dashboard
          port: 3000
          public: true
          path: /
          healthPath: /
        - name: backend
          sourcePath: .
          dockerfile: modules/api/Dockerfile
          port: 8080
          public: true
          path: /api
          healthPath: /health
          env:
            ASPNETCORE_ENVIRONMENT: Staging
    secrets:
      municloud_api_key: ${{ secrets.MUNICLOUD_API_KEY }}
      postgres_password: ${{ secrets.MUNICLOUD_POSTGRES_PASSWORD }}
```

## Inputs

| Input | Required | Default | Description |
| --- | --- | --- | --- |
| `app_id` | Yes | | App id from the Municloud console URL. Pass `${{ vars.MUNICLOUD_APP_ID }}` from the caller workflow. |
| `database` | No | `sqlite` | `sqlite` or `postgres`. |
| `minicloud_environment` | No | `prod` | `prod` or `staging`. `prod` uses the production Minicloud API. `staging` uses `https://municloud-dev.muni.dev/api`. |
| `services` | Yes | | YAML service array. Uses the same service fields as `municloud.yml`, with `name` added because a workflow input cannot receive the keyed `services` map directly. |
| `image_tag` | No | commit SHA | Docker image tag. |

## Services

`services` accepts a YAML array with one mapping per service:

```yaml
services: |
  - name: backend
    sourcePath: .
    dockerfile: Dockerfile
    port: 8080
    public: true
    path: /
    healthPath: /health
```

| Field | Required | Description |
| --- | --- | --- |
| `name` | Yes | Service name. Must use lowercase letters, numbers, dashes, and underscores. |
| `sourcePath` | Required when the workflow builds the image | Docker build context. |
| `dockerfile` | No | Dockerfile path. Defaults to Docker's normal lookup in `sourcePath`. |
| `image` | Required for prebuilt image services; optional when `sourcePath` is set | Full image reference. If omitted for a built service, the workflow publishes `ghcr.io/<owner>/<repo>/<service>:<sha>`. |
| `port` | Yes | Internal container port, `1` to `65535`. |
| `public` | Yes | Whether the service receives public HTTP traffic. At least one service must be public. |
| `path` | Yes | Public route path. Must start with `/`. |
| `healthPath` | Yes | HTTP health check path. Must start with `/`. |
| `env` | No | String key/value environment variables for the service. |

## What The Workflow Does

The workflow runs two jobs:

- `minicloud - publish`: builds configured service images, pushes them to GHCR, and creates the service payload.
- `minicloud - deploy`: calls the Minicloud API for the configured app id, starts a deployment, and waits for completion.

## Image Names

Images are published to:

```text
ghcr.io/<owner>/<repo>/<service>:<sha>
```

Use `image_tag` to override the tag.

## Postgres

When `database: postgres`, pass:

```yaml
secrets:
  postgres_password: ${{ secrets.MUNICLOUD_POSTGRES_PASSWORD }}
```

For SQLite, omit `postgres_password`.
