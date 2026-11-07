# Minicloud observability (T10): private Loki + Grafana OSS

Beta application-log storage for Minicloud self-deploy. Single-binary Loki
plus Grafana OSS, colocated with Minicloud services on their own
provider-backed volume. This document records the T10 infrastructure
decisions, pinned versions, and operating procedures.

Spec: `features/product/grafana-application-logs/1.grafana-application-logs.md`
(runtime requirements, lines 214-243) and
`features/product/grafana-application-logs/subspecs/10.ingestion-and-storage.md` (T10).

## Pinned versions (recorded 2026-09-08)

| Component | Version | Image digest |
| --- | --- | --- |
| Loki | 3.5.2 | `grafana/loki:3.5.2@sha256:d0a95e651bb7d0a5a6468035c5b49c52f4678d90d598f853c06716bca102686b` (multi-arch manifest list; amd64 member `sha256:d12ab5b1cd7ac58c1c0ea8cb43e0d151d1d5bee1cb897c7e7eaf225adae07928`) |
| Grafana OSS | 11.6.5 | `grafana/grafana-oss:11.6.5@sha256:d552f949693c54d17c6f867ff6aeb128b021e54e923895dcf9cd6aa8176c0d74` (single-arch amd64 manifest) |

Rules:

- Never use `latest`. Every reference (dev compose, `remote-deploy.sh`
  `LOKI_IMAGE`/`GRAFANA_IMAGE`) pins tag + digest.
- Configs were validated live against these exact images: Loki `/ready`
  reaches 200, unauthenticated access returns `no org id`/`401`,
  tenant-scoped push + query round-trips, cross-tenant reads return empty,
  old-sample rejection works, Grafana `/api/health` is ok with anonymous
  access denied (`401` on `/api/org`).
- The Grafana 11.6.5 image is amd64-only at this pin; on arm64 dev machines
  it runs under emulation (slower startup, fine for local use). Production
  VPSs are amd64.
- Container data owners (asserted, never auto-repaired on nonempty data):
  Loki `10001:10001`, Grafana `472:472` (verified via image inspect).

## Layout

```text
repo:
  compose.observability.yml            # dev/local stack (merge with compose.dev.yml)
  observability/loki/loki.yml         # reviewed Loki template
  observability/grafana/grafana.ini   # reviewed Grafana template
  observability/grafana/provisioning/ # intentionally empty; T20 provisions dynamically
  observability/README.md             # this file

VPS (/var/lib/minicloud/observability, separate mounted volume):
  loki/        -> /loki in the Loki container (chunks, TSDB index, WAL, compactor state)
  grafana/     -> /var/lib/grafana in the Grafana container (grafana.db, sessions, plugins)

/opt/minicloud/apps/minicloud/shared/observability/ (re-rendered each deploy):
  loki.yml, grafana.ini, provisioning/ (empty until T20), grafana-admin-password (0600)
```

Key config facts: `auth_enabled: true`,
`querier.multi_tenant_queries_enabled: false`, TSDB schema v13 with 24h
index period, WAL enabled, compactor with `retention_enabled: true`,
`retention_period: 48h` (2 days), `deletion_mode: filter-and-delete`,
structured metadata allowed. Grafana: anonymous off, sign-up off,
`auto_assign_org = false`, login form disabled, auth proxy enabled with
`enable_login_token = false`, secure `SameSite=Lax` cookies. Full rationale
is commented in the templates.

## Local dev

```bash
docker compose -f compose.dev.yml -f compose.observability.yml config  # render check
docker compose -f compose.dev.yml -f compose.observability.yml up -d loki grafana
curl http://127.0.0.1:3100/ready
curl http://127.0.0.1:3000/api/health
# Grafana UI: http://localhost:3000 (admin / minicloud-dev-only)
```

Ports bind to 127.0.0.1 only. Production publishes **no** host ports.

## Production behavior (remote-deploy.sh)

- App deployment validates, pulls, and starts only app services, Postgres,
  and OpenBao. Observability configuration is excluded from those commands.
- The workflow's optional observability-volume setup has a five-minute limit;
  its failure does not stop deployment.
- After app health checks, Caddy activation, and deployment recording, a
  separate shell attempts observability setup for at most 180 seconds (plus
  a ten-second forced-termination grace period). Errors produce a warning,
  and the successful app deployment still emits its completion marker.
- Missing mounts/directories or unsafe ownership skip the log stack. There is
  no root-disk fallback and no automatic ownership repair for nonempty data.
- Rendering, configuration validation, image pulls, container startup, and
  health checks belong to this optional attempt. A failure stops that attempt
  without failing or rolling back app services.
- Both Compose invocations retain the same project/network so the API can
  reach Loki/Grafana privately. They use separate config files and never use
  `--remove-orphans`; omitting optional containers must not delete them.
  Service removal requires an explicit operation.
- App services never depend on Loki/Grafana. App health checks remain separate
  from observability health checks.

## Version upgrade procedure

1. Pick the new Loki 3.x / Grafana 11.x tags. Record tag + digest
   (`docker buildx imagetools inspect <image>:<tag>`).
2. Read the upstream changelog for breaking config/auth-API changes,
   especially Grafana `auth.proxy` options and Loki schema/TSDB changes.
3. Update in one change: this README's table, `compose.observability.yml`,
   `remote-deploy.sh` (`LOKI_IMAGE`/`GRAFANA_IMAGE`), and the templates if
   option names changed.
4. Validate locally: boot both images with the new templates, repeat the
   T10 live checks (ready/health, anonymous denied, tenant roundtrip,
   old-sample rejection), and run
   `docker compose -f compose.dev.yml -f compose.observability.yml config`.
5. Roll to staging first; confirm `/ready`, `/api/health`, Grafana login
   via the T20 proxy, and compactor activity before promoting.

## Rollback constraints

- **Grafana**: `grafana.db` migrations run forward on startup. Downgrading
  across a version that migrated the database requires restoring
  `grafana.db` from a pre-upgrade backup (below); a binary-only rollback
  may fail to start. Patch/minor downgrades within the same migration level
  are normally safe but must be proven on staging.
- **Loki**: TSDB schema v13 is forward-compatible for reads of segments
  written by the same major line. Never roll back across a schema change
  without the Loki upgrade docs' explicit guidance; when in doubt, restore
  the volume from backup instead of downgrading the binary.
- Config rollback is always safe: re-deploy the previous known-good
  templates + digests; `render_observability_configs` rewrites them.

## Backup / restore

Beta promise: Grafana state and Loki configuration are recoverable. Raw log
retention is 2 days best-effort; **no log backup or availability SLA** is
promised in beta.

- **Grafana**: stop the Grafana container
  (`docker compose -p minicloud-minicloud stop grafana`), copy
  `/var/lib/minicloud/observability/grafana/grafana.db` plus
  `/opt/minicloud/apps/minicloud/shared/observability/grafana.ini` and the
  `grafana-admin-password` file to encrypted offline storage, restart.
  The admin password and `grafana.db` encryption material must be backed up
  together; one without the other is unrestorable.
- **Loki**: crash recovery is via the WAL on the same volume; host/volume
  failure can lose unflushed data (documented beta limitation). For a
  point-in-time copy, stop Loki first, then snapshot or copy the whole
  `/var/lib/minicloud/observability/loki` tree.
- **Isolated restore test** (required before relying on backups): restore
  to a disposable host or directory, start Loki/Grafana against the copy
  with the backed-up configs, and verify login + a historical query. Never
  restore untrusted state over the live volume without a fresh backup of
  the live volume first.
- T20 moves the Grafana server-admin credential into OpenBao/operator
  secret storage; until then the `grafana-admin-password` file is part of
  every backup.

## Disk-pressure recovery

Filesystem Loki does **not** delete data merely because the disk is full;
ingestion will fail while the disk stays full.

1. Alert at 70% volume use; ingestion is rejected at 85% and resumes below
   75% (operator-enforced thresholds; see capacity math below).
2. If full: stop Loki, verify which tenant/time ranges dominate
   (`du` on chunk dirs, Loki query stats), then either expand the volume
   (preferred; Hetzner volume resize + `resize2fs`) or lower retention and
   let the compactor catch up after restart. Deleting chunk files by hand
   corrupts the index; use retention + delete requests only.
3. Keep reads/compaction available where possible; keep the API rollout
   independent (it never waits on this stack).
4. After recovery, confirm compactor lag returns to zero and the 2-day
   retention window holds before re-enabling any paused ingestion.

## Capacity math (placeholders — measure before hosted enablement)

Provision disk from first principles; compression savings are NOT assumed:

```text
required_bytes =
    admitted_bytes_per_day x retention_days (2) x storage_factor (1.0)
    + WAL (measured peak, budget >= 2 GiB)
    + TSDB index + compactor working space (measure; budget >= 20% of retained bytes)
    + headroom >= 30%
```

Beta defaults that bound the inputs (enforced by the T12 gateway, supported
by Loki `limits_config`):

- Per-app sustained ingest 64 KiB/s, burst 1 MiB; per-app daily budget
  100 MiB; per-organization daily budget 250 MiB; whole-stack daily budget
  1 GiB across customers.
- Query: 1,000 entries, 4 MiB response, 10 s timeout, max 7-day range;
  concurrency 2 per app, 8 total.

Host sizing starter (must be validated by load test, not assumed):
Loki limit 1 CPU / 1 GiB, Grafana limit 0.5 CPU / 512 MiB
(see `deploy.resources` in `compose.observability.yml`), plus control-plane
headroom. If colocation cannot protect control-plane responsiveness, move
observability to a separate VPS. Record measured numbers here when T90
runs.
