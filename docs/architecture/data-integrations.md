# Data, storage, and integrations

This document names Municloud data, storage, and external integration surfaces.

## V0 data

V0 should avoid a database. State lives in:

- `municloud.yml` in the application repository
- GitHub Actions secrets and variables
- provider API token in GitHub Actions secrets
- provider server ID/IP/SKU metadata
- GHCR images and tags
- server-side app environment files
- `/opt/municloud/apps/<app>/deploys.json`
- Docker Compose project state
- Caddy config fragments

## V1 data

V1 is the first mini-SaaS release and should include a hosted control-plane database.

The control plane should store only the data needed for product experience:

- users and teams
- linked GitHub installations
- provider account/project inventory
- server registrations
- provider server IDs and SKU mappings
- app registrations
- deploy history snapshots
- audit events
- notification settings
- subscription and billing state
- cost and markup snapshots

Avoid storing long-lived per-server SSH private keys in the control plane in V1.

## GitHub

V1 should use GitHub as the source and build surface. The preferred productized path is GitHub App plus generated or managed GitHub Actions workflow. GitHub is responsible for:

- source control
- CI checks
- Docker image build
- image push to GHCR
- build job execution
- deployment job execution when using the Actions-orchestrated path
- secrets needed by the deployment workflow

The control plane should receive GitHub App/webhook signals for repository connection, push events, installation state, and deploy visibility.

Until webhook ingestion is complete, the control-plane API can refresh a
deployment from GitHub on demand through
`POST /v1/deployments/{deploymentId}/refresh`. This endpoint uses the same
workflow-run lookup and status mapping as the background deployment monitor and
updates the stored deployment status, GitHub run link, failure metadata, and
timeline events when GitHub has new state.

## GHCR

GitHub Container Registry is the default image registry.

For CLI-first V1 deployments, Municloud should expose `registry-dev.muni.dev`
for dev/staging and `registry.muni.dev` for production as GHCR-backed
Docker/OCI upload proxies. Docker clients push through the environment registry
host; Municloud validates tenant-scoped registry tokens and
forwards upload-related registry protocol calls to a Municloud-owned GHCR
namespace with server-side credentials. The runtime pulls the resulting
Municloud-owned GHCR refs directly with Municloud-managed pull auth. This avoids
customer GHCR pull-token setup and avoids Municloud owning registry blob storage.
The CLI, registry proxy, and internal deployment workflow must agree on the
same upstream owner, defaulting to `municloud`; otherwise the proxy can publish
to one GHCR namespace while deployments validate and pull from another.

Image tags:

- commit SHA tag for every deployable build
- optional `latest` tag for convenience
- optional environment tag later

The runtime should deploy immutable SHA tags by default.

## Domains and DNS

Municloud config owns app domain intent, but users own DNS. The CLI can verify DNS records and Caddy certificate readiness, but V1 does not need to manage DNS providers.

## Secrets

Secrets live in two places:

- GitHub Actions secrets for build/deploy credentials
- server-side app env files under `/opt/municloud/apps/<app>/shared/env`

Municloud must never commit secrets to git. Secret listing commands must print names and metadata only.

## Telemetry

V0 has no product telemetry requirement.

V1 should include product and operational telemetry for the mini-SaaS, including deploy status, API errors, runtime connectivity, and billing events. CLI telemetry should be opt-in or clearly disclosed with an opt-out path.

## Future integrations

Later versions may add:

- additional DigitalOcean, Hetzner, Vultr, and Linode server provisioning adapters
- DNS provider automation
- Slack/email deploy notifications
- backup storage providers
- control-plane audit logs
