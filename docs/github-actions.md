# GitHub Actions Workflow

Use the reusable Minicloud workflow when you want GitHub Actions to build images, push them to GHCR, and ask the Minicloud API to start a deployment.

## Prerequisites

Create a Minicloud API key in the console with these scopes:

- `deployments:create`
- `deployments:read`

Add these secrets to your app repository. Repository or organization secrets work.

| Secret | Required | Description |
| --- | --- | --- |
| `MINICLOUD_API_KEY` | Yes | Minicloud API key from the console. |
| `MINICLOUD_POSTGRES_PASSWORD` | Only for `database: postgres` | Postgres password used by the deployment. |
| `MINICLOUD_SERVICE_ENV_SECRETS` | Only when using `secretEnv` | JSON object of runtime environment secret values, for example `{"MAPBOX_ACCESS_TOKEN":"..."}`. |

Add this repository or organization variable:

| Variable | Required | Description |
| --- | --- | --- |
| `MINICLOUD_APP_ID` | Yes | App id from the Minicloud console URL, for example `app_52kqllu8k1nq4kg352kqllu8`. |

## Managed VPS Plans

The workflow deploys to the app selected by `MINICLOUD_APP_ID`. The app's managed VPS plan is selected in the Minicloud control plane, not in the customer workflow.

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
      app_id: ${{ vars.MINICLOUD_APP_ID }}
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
      minicloud_api_key: ${{ secrets.MINICLOUD_API_KEY }}
      postgres_password: ${{ secrets.MINICLOUD_POSTGRES_PASSWORD }}
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
      app_id: ${{ vars.MINICLOUD_APP_ID }}
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
          path: /
          healthPath: /health
          env:
            ASPNETCORE_ENVIRONMENT: Staging
          secretEnv:
            MAPBOX_ACCESS_TOKEN: MAPBOX_ACCESS_TOKEN
    secrets:
      minicloud_api_key: ${{ secrets.MINICLOUD_API_KEY }}
      postgres_password: ${{ secrets.MINICLOUD_POSTGRES_PASSWORD }}
      service_env_secrets: ${{ secrets.MINICLOUD_SERVICE_ENV_SECRETS }}
```

## Inputs

| Input | Required | Default | Description |
| --- | --- | --- | --- |
| `app_id` | Yes | | App id from the Minicloud console URL. Pass `${{ vars.MINICLOUD_APP_ID }}` from the caller workflow. |
| `database` | No | `sqlite` | `sqlite` or `postgres`. |
| `minicloud_environment` | No | `prod` | `prod` or `staging`. `prod` uses the production Minicloud API. `staging` uses `https://api.cloud-dev.muni.dev`. |
| `services` | Yes | | YAML service array. Uses the same service fields as `minicloud.yml`, with `name` added because a workflow input cannot receive the keyed `services` map directly. |
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
| `secretEnv` | No | String key/value references to keys in `service_env_secrets`. Use this for sensitive runtime environment variables. |

## Runtime Secrets

Do not put `${{ secrets.MY_SECRET }}` inside the `services` input. GitHub does not expose the `secrets` context inside reusable workflow `with` inputs, and secret values could be copied into publish-job outputs.

For sensitive runtime environment variables, put references in `secretEnv`:

```yaml
services: |
  - name: backend
    sourcePath: .
    dockerfile: Dockerfile
    port: 8080
    public: true
    path: /
    healthPath: /health
    secretEnv:
      MAPBOX_ACCESS_TOKEN: MAPBOX_ACCESS_TOKEN
secrets:
  service_env_secrets: ${{ secrets.MINICLOUD_SERVICE_ENV_SECRETS }}
```

Store `MINICLOUD_SERVICE_ENV_SECRETS` as one GitHub repository or organization secret containing JSON:

```json
{
  "MAPBOX_ACCESS_TOKEN": "actual-token-value"
}
```

The publish job keeps only `secretEnv` references. The deploy job merges those references with `service_env_secrets` immediately before it calls the Minicloud API.

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
  postgres_password: ${{ secrets.MINICLOUD_POSTGRES_PASSWORD }}
```

For SQLite, omit `postgres_password`.
