# Minicloud

Deploy your apps to your own VPS. Minicloud is a self-hosted deployment platform for individual developers running their own projects.

- [Self-hosting](docs/self-hosting.md)
- [CLI reference](docs/cli.md)
- [Durable app storage](docs/durable-storage.md)
- [GitHub Actions](docs/github-actions.md)
- [Report a vulnerability](SECURITY.md)
- [Licensing](docs/licensing.md)

Minicloud includes a CLI, web dashboard, API, deployment worker and VPS runtime. The worker deploys Docker services, configures routing and HTTPS, and reports deployment status and logs.

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
