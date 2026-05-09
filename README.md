# Minicloud

Public distribution repository for the Municloud CLI and reusable customer deployment workflows.

## Install Municloud CLI

Latest binaries are attached to [GitHub Releases](https://github.com/muniventures/minicloud/releases/latest). Use the install script for your OS:

### macOS

```bash
curl -fsSL https://raw.githubusercontent.com/muniventures/minicloud/main/install.sh | sh
```

### Linux

```bash
curl -fsSL https://raw.githubusercontent.com/muniventures/minicloud/main/install.sh | sh
```

### Windows

Run in PowerShell:

```powershell
iwr https://raw.githubusercontent.com/muniventures/minicloud/main/install.ps1 -useb | iex
```

### Specific Version

```bash
curl -fsSL https://raw.githubusercontent.com/muniventures/minicloud/main/install.sh | MUNICLOUD_VERSION=v0.1.0 sh
```

```powershell
$env:MUNICLOUD_VERSION = "v0.1.0"; iwr https://raw.githubusercontent.com/muniventures/minicloud/main/install.ps1 -useb | iex
```

### Custom Install Directory

```bash
curl -fsSL https://raw.githubusercontent.com/muniventures/minicloud/main/install.sh | MUNICLOUD_INSTALL_DIR="$HOME/.local/bin" sh
```

```powershell
$env:MUNICLOUD_INSTALL_DIR = "$env:USERPROFILE\.municloud\bin"; iwr https://raw.githubusercontent.com/muniventures/minicloud/main/install.ps1 -useb | iex
```

Verify:

```bash
municloud --help
```

Show configured CLI environment defaults:

```bash
municloud --env
```

## Customer Deploy Workflow

Customer repositories can call the reusable workflow instead of copying the deployment implementation:

```yaml
jobs:
  minicloud:
    uses: muniventures/minicloud/.github/workflows/customer-deploy.yml@main
    with:
      app_name: teamcore
      environment: staging
      deployment_type: backend_frontend
      database: postgres
      aspnetcore_environment: Staging
      frontend_context: ./modules/ui/dashboard
      backend_context: .
      backend_dockerfile: modules/api/Dockerfile
    secrets:
      municloud_api_key: ${{ secrets.MUNICLOUD_API_KEY }}
      postgres_password: ${{ secrets.MUNICLOUD_POSTGRES_PASSWORD }}
```

The caller repository still owns source checkout, Docker image build context, package publishing permissions, and customer secrets. `MUNICLOUD_API_KEY` is generated in the Municloud console and must have `apps:read`, `apps:write`, `deployments:create`, and `deployments:read` scopes. Put caller secrets at repository or organization scope; GitHub environment secrets are not available to reusable workflow caller jobs.
