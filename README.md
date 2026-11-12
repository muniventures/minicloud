# Minicloud

Deploy your apps to your own VPS. Minicloud is a self-hosted deployment platform for individual developers running their own projects.

- [Self-hosting](docs/self-hosting.md)
- [CLI reference](docs/cli.md)
- [First control-plane seed](docs/seed.md) (source implementation; publication/qualification pending)
- [API contract](docs/api.md)
- [Providers and plans](docs/providers.md)
- [Durable app storage](docs/durable-storage.md)
- [GitHub Actions](docs/github-actions.md)
- [Report a vulnerability](SECURITY.md)
- [Licensing](docs/licensing.md)

Minicloud includes a CLI, web dashboard, API, deployment worker and VPS runtime. The worker deploys Docker services, configures routing and HTTPS, and reports deployment status and logs.

## Capabilities

- **VPS provisioning:** Connect Hetzner, DigitalOcean or Vultr accounts; define named plans by account, region and server size.
- **Multi-service apps:** Deploy public web services and private workers together, or update individual services from separate configs.
- **Builds and images:** Detect common Node.js, .NET and Spring Boot projects and generate Dockerfiles. Build locally into a private registry, upload source for server builds, or deploy prebuilt images.
- **Environments and previews:** Group named environments and Git branch previews under one app, with separate runtime configuration, databases, secrets and domains. See the [application/environment upgrade guide](docs/application-environment-upgrade.md) for existing installations.
- **Domains and HTTPS:** Get generated service subdomains, attach verified custom domains and provision TLS certificates automatically. Generated DNS records can be managed through Cloudflare.
- **Configuration and secrets:** Set per-service environment variables and app- or service-scoped secrets stored in OpenBao.
- **Databases:** Run SQLite or PostgreSQL with your app, or bind apps to shared or dedicated PostgreSQL servers. Rotate credentials, migrate databases and manage access; private database networks require Hetzner.
- **Backups and recovery:** Schedule encrypted database backups to separate repository apps, configure retention, download backup files and restore databases.
- **Persistent storage:** Mount named, expandable Hetzner volumes that survive deployments and app deletion. Other providers are not supported for these volumes.
- **Asset releases:** Build and publish versioned assets separately from services; update unpinned mounts without restarting services or pin a specific release.
- **Deployment operations:** Track progress, history, events and build logs; cancel deployments and inspect health-check failures.
- **Logs and diagnostics:** Filter runtime logs by service, source and time; inspect container health and agent status. Optional application logging adds OpenTelemetry ingestion and Grafana exploration.
- **Automation and access:** Deploy through the CLI, HTTP API or reusable GitHub Actions workflow. Use scoped organization API keys and switch between self-hosted instances.

## Develop from source

Use .NET 10, Node.js 22, and Docker with Compose. From the repository root:

```sh
dotnet test Minicloud.slnx --configuration Release -m:1
npm --prefix modules/dashboard ci
npm --prefix modules/dashboard test
npm --prefix modules/dashboard run build
dotnet run --project modules/cli -- --help
```

Some integration tests require Docker. For local PostgreSQL and OpenBao, use
`docker compose -f compose.dev.yml up -d postgres openbao openbao-init`.
This Compose file is for development. Use the self-hosting guide to configure
a persistent installation.
