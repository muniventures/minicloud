# GitHub Actions Workflow

Use the reusable Minicloud workflow when you want GitHub Actions to build images, push them to GHCR, and ask the Minicloud API to start a deployment.

## Prerequisites

Create a Minicloud API key in the console with these scopes:

- `deployments:create`
- `deployments:read`

Add these secrets to your app repository. Repository or organization secrets work. Environment secrets also work when they are defined in the GitHub environment passed as the workflow `environment` input.

| Secret | Required | Description |
| --- | --- | --- |
| `MUNICLOUD_API_KEY` | Yes | Minicloud API key from the console. |
| `MUNICLOUD_POSTGRES_PASSWORD` | Only for `database: postgres` | Postgres password used by the deployment. |

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
      minicloud_app_id: app_52kqllu8k1nq4kg352kqllu8
      database: postgres
      backend_context: .
      backend_dockerfile: Dockerfile
      backend_port: "8080"
      backend_path: /
      backend_health_path: /health
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
      minicloud_app_id: app_52kqllu8k1nq4kg352kqllu8
      database: postgres
      aspnetcore_environment: Staging
      frontend_context: ./modules/ui/dashboard
      frontend_port: "3000"
      frontend_path: /
      frontend_health_path: /
      backend_context: .
      backend_dockerfile: modules/api/Dockerfile
      backend_port: "8080"
      backend_path: /api
      backend_health_path: /health
    secrets:
      municloud_api_key: ${{ secrets.MUNICLOUD_API_KEY }}
      postgres_password: ${{ secrets.MUNICLOUD_POSTGRES_PASSWORD }}
```

## Inputs

| Input | Required | Default | Description |
| --- | --- | --- | --- |
| `minicloud_app_id` | Yes | | App id from the Minicloud console URL, for example `app_52kqllu8k1nq4kg352kqllu8`. |
| `plan` | No | `P0` | Minicloud plan. |
| `aspnetcore_environment` | No | empty | Adds `ASPNETCORE_ENVIRONMENT` to backend service env when set. |
| `database` | No | `sqlite` | `sqlite` or `postgres`. |
| `municloud_api_url` | No | `https://cloud.muni.dev/api` | API URL override. |
| `image_tag` | No | commit SHA | Docker image tag. |
| `frontend_context` | No | `./frontend` | Frontend Docker build context. |
| `frontend_dockerfile` | No | empty | Frontend Dockerfile path. |
| `frontend_port` | No | `3000` | Frontend container port. |
| `frontend_path` | No | `/` | Frontend public route path. |
| `frontend_health_path` | No | `/` | Frontend health check path. |
| `backend_context` | No | `.` | Backend Docker build context. |
| `backend_dockerfile` | No | empty | Backend Dockerfile path. |
| `backend_port` | No | `8080` | Backend container port. |
| `backend_path` | No | `/api` | Backend public route path. |
| `backend_health_path` | No | `/health` | Backend health check path. |

## What The Workflow Does

The workflow runs three jobs:

- `minicloud - build`: builds Docker images for the configured services.
- `minicloud - publish`: pushes images to GHCR and creates the service payload.
- `minicloud - deploy`: calls the Minicloud API for the configured app id, starts a deployment, and waits for completion.

## Image Names

Images are published to:

```text
ghcr.io/<owner>/<repo>/frontend:<sha>
ghcr.io/<owner>/<repo>/backend:<sha>
```

Use `image_tag` to override the tag.

## Postgres

When `database: postgres`, pass:

```yaml
secrets:
  postgres_password: ${{ secrets.MUNICLOUD_POSTGRES_PASSWORD }}
```

For SQLite, omit `postgres_password`.
