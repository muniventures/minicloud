# Codex entry point

Never push code unless explicitly asked.
Never make unsupported inferences. Verify from source or state uncertainty.

Use `docs/architecture/agent-instructions.md` as the shared instruction source for GitHub Copilot, Claude Code, Codex, and other repository-aware agents.

Before doing any work, read these docs in order:

1. `docs/architecture/README.md`
2. `docs/architecture/agent-instructions.md`
3. `docs/architecture/runtime.md` if touching VPS runtime, deployment scripts, Docker, Caddy, systemd, SSH, or health checks
4. `docs/architecture/cli.md` if touching the Minicloud CLI, config format, command contracts, or generated files
5. `docs/architecture/data-integrations.md` if touching GitHub, GHCR, registry auth, domains, secrets, storage, telemetry, or future control-plane data
6. `docs/architecture/provisioning-and-billing.md` if touching VPS provider APIs, server plans, provisioning, teardown, pricing, markup, invoicing, or cost reconciliation
7. `docs/architecture/security.md` if touching auth, SSH, secrets, workflow permissions, deploy users, privilege boundaries, or audit trails

If your change affects architecture, update the relevant files in `docs/architecture/` in the same change.

This file should stay thin. Shared project guidance belongs in `docs/architecture/agent-instructions.md` and the other architecture docs.
