# Local Registry Proxy Testing

Use the local registry proxy to test the CLI Docker auth/build/push path
without Municloud DNS.

## VS Code HTTPS Proxy

The normal local path runs the Municloud API, dashboard, and registry proxy
from VS Code. The registry proxy listens on `https://localhost:5050`, which
matches Docker's default expectation for a registry.

Trust the ASP.NET Core development certificate once:

```bash
dotnet dev-certs https --trust
```

Start this VS Code compound:

```text
Municloud API + Dashboard + Registry
```

Make sure `modules/registry/appsettings.Development.json` has:

```json
{
  "Registry": {
    "UpstreamUsername": "your-github-username",
    "UpstreamToken": "your-github-token"
  }
}
```

Then run the CLI from TeamCore:

```bash
cd /Users/muniperez/Code/workspaces/teamcore/main

MUNICLOUD_API_URL=http://localhost:3210 \
MUNICLOUD_REGISTRY_HOST=localhost:5050 \
MUNICLOUD_RUNTIME_REGISTRY_PREFIX=ghcr.io/municloud \
/Users/muniperez/Code/workspaces/municloud/main/artifacts/cli/osx-arm64-framework-dependent/municloud deploy --publish-only
```

If Docker reports a certificate trust error, restart Docker Desktop after
trusting the dev certificate.

## Static Local Upstream Registry

This alternate harness uses a throwaway local upstream `registry:2`. It runs
over HTTP and requires Docker insecure-registry configuration for
`localhost:5050`, so the VS Code HTTPS path above is preferred.

Start the local proxy and throwaway upstream registry:

```bash
cd /Users/muniperez/Code/workspaces/municloud/main
docker compose -f docker-compose.local-registry.yml up --build
```

In another shell, run the CLI from a project that has a `municloud.yml` with
service `sourcePath` values:

```bash
cd /Users/muniperez/Code/workspaces/teamcore/main

MUNICLOUD_TOKEN=local-token \
MUNICLOUD_REGISTRY_HOST=localhost:5050 \
MUNICLOUD_RUNTIME_REGISTRY_PREFIX=localhost:5051/municloud-local \
MUNICLOUD_LOCAL_ORGANIZATION_SLUG=local \
/Users/muniperez/Code/workspaces/municloud/main/artifacts/cli/osx-arm64-framework-dependent/municloud deploy --publish-only
```

What this exercises:

- CLI config loading
- Docker login to the proxy
- Docker image build
- Docker push to `localhost:5050`
- Proxy upload forwarding to the local upstream registry on `localhost:5051`
- Runtime image-name translation printed by the CLI

Inspect pushed images in the upstream registry:

```bash
curl http://localhost:5051/v2/_catalog
```

For production-like API auth, run the registry proxy with
`Registry__AuthMode=Api` and point `Registry__ApiBaseUrl` at the local API.
The default local harness intentionally uses `AuthMode=Static` so the Docker
push path can be tested without bootstrapping users, apps, and API keys first.
