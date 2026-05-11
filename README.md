# Minicloud

Minicloud deploys containerized apps to managed infrastructure from either the `minicloud` CLI or a GitHub Actions workflow.

## How To Use Minicloud

- [CLI documentation](docs/cli.md)
- [GitHub Actions workflow documentation](docs/github-actions.md)

## Install CLI

macOS and Linux:

```bash
curl -fsSL https://raw.githubusercontent.com/muniventures/minicloud/main/install.sh | sh
```

Windows PowerShell:

```powershell
iwr https://raw.githubusercontent.com/muniventures/minicloud/main/install.ps1 -useb | iex
```

Verify:

```bash
minicloud --help
```

Show environment defaults:

```bash
minicloud --env
```

