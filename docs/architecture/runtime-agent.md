# Runtime Agent Runbook

Minicloud-managed VPSes run `minicloud-agent` to push runtime logs and status to the hosted API over outbound HTTPS. The control plane never needs inbound SSH to read application console data.

## Files

- Config: `/opt/minicloud/agent/config.json`
- Runtime token: `/opt/minicloud/agent/runtime-token`
- Binary: `/opt/minicloud/agent/minicloud-agent`
- State and cursors: `/var/lib/minicloud-agent/state.json`
- Systemd unit: `/etc/systemd/system/minicloud-agent.service`

## Service Commands

```bash
systemctl status minicloud-agent
journalctl -u minicloud-agent -n 200 --no-pager
systemctl restart minicloud-agent
```

The first release runs as `minicloud-agent` with Docker group membership so it can read Docker logs. Caddy logs are collected from journald.

## Install Or Upgrade

The deploy workflow should:

1. Create or refresh the runtime server registration with `POST /v1/apps/{appId}/runtime-servers`.
2. Write the returned token to `/opt/minicloud/agent/runtime-token` with mode `0600`.
3. Write `config.json` with server/app identity and API base URL.
4. Install the binary and `minicloud-agent.service`.
5. Run `templates/runtime/install-minicloud-agent.sh`.
6. Confirm `lastSeenAt` updates through `GET /v1/apps/{appId}/runtime-status`.

The install script is idempotent and preserves `/var/lib/minicloud-agent/state.json`.

## Token Rotation

1. Call `POST /v1/apps/{appId}/runtime-servers/{serverId}/rotate-token`.
2. Write the new raw token to `/opt/minicloud/agent/runtime-token`.
3. Restart `minicloud-agent`.
4. Wait for heartbeat.
5. Revoke the old runtime token after overlap once old heartbeats stop.

Raw runtime tokens are never stored in the database. Only token prefix and PBKDF2 hash are persisted.

## Offline Agents

The dashboard marks an agent offline when `lastSeenAt` is older than two minutes. Check:

- VPS network/DNS access to the Minicloud API.
- Token file permissions and freshness.
- `journalctl -u minicloud-agent`.
- Docker group membership for the `minicloud-agent` user.
- Disk pressure under `/var/lib/minicloud-agent`.

## Storage Pressure

The API prunes runtime logs during ingest. The first release keeps seven days and also enforces a per-app row cap. The agent keeps only a small cursor state file; if future buffering is enabled, it must be capped by bytes and age.

## 502 Support Flow

For a public app returning 502, open the app Console and inspect:

- Caddy records containing reverse proxy errors or 502s.
- Service stderr records for startup or health failures.
- Runtime status for container state, health, image, and restart count.
- Latest deployment URL and service port/path expectations.

This path intentionally points to evidence and does not execute remote commands.
