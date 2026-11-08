#!/usr/bin/env bash
set -Eeuo pipefail
umask 077

if [ "$#" -lt 1 ] || [ "$#" -gt 2 ] || { [ "$#" -eq 2 ] && [ "$2" != "--observability" ]; }; then
  echo "Usage: remote-deploy.sh <payload-json>"
  exit 2
fi

PAYLOAD_FILE="$1"
if [ ! -f "${PAYLOAD_FILE}" ]; then
  echo "Deploy payload ${PAYLOAD_FILE} does not exist."
  exit 1
fi

# SSH can accept connections before cloud-init installs the runtime packages.
# Wait before parsing the payload: even jq may not exist on the first boot.
if command -v cloud-init >/dev/null 2>&1; then
  echo "Waiting for cloud-init bootstrap before deployment..."
  bootstrap_status=0
  timeout 600 sudo -n cloud-init status --wait >/dev/null 2>&1 || bootstrap_status=$?
  case "${bootstrap_status}" in
    0) ;;
    2) echo "Cloud-init completed with recoverable warnings; validating runtime prerequisites." ;;
    *)
      echo "__MINICLOUD_DEPLOY_FAILURE__=bootstrap_failed"
      echo "ERROR: cloud-init bootstrap failed (exit ${bootstrap_status}; timeout 600 seconds)." >&2
      exit 1
      ;;
  esac
fi
if ! command -v jq >/dev/null 2>&1; then
  echo "__MINICLOUD_DEPLOY_FAILURE__=bootstrap_failed"
  echo "ERROR: runtime bootstrap is incomplete: jq is missing." >&2
  exit 1
fi

# cloud-init may add this account to docker after the SSH session starts.
# Refresh only that authorized membership before using the Docker socket.
if [[ " $(id -nG) " != *" docker "* ]] && [[ " $(id -nG "$(id -un)") " == *" docker "* ]]; then
  if [[ "${MINICLOUD_DOCKER_GROUP_REFRESHED:-}" == 1 ]] || ! command -v sg >/dev/null 2>&1; then
    echo "__MINICLOUD_DEPLOY_FAILURE__=bootstrap_failed"
    echo "ERROR: runtime bootstrap could not activate Docker group membership." >&2
    exit 1
  fi
  echo "Refreshing Docker group membership after runtime bootstrap..."
  refresh_command="exec bash"
  shell_quote_escape="'\\''"
  for refresh_arg in "${BASH_SOURCE[0]}" "$@"; do
    refresh_command+=" '${refresh_arg//\'/$shell_quote_escape}'"
  done
  export MINICLOUD_DOCKER_GROUP_REFRESHED=1
  exec sg docker -c "$refresh_command"
fi

payload_string() {
  jq -r "$1 // \"\"" "${PAYLOAD_FILE}"
}

APP_NAME="$(payload_string '.app_name')"
CUSTOMER="$(payload_string '.customer')"
APP_ENV="$(payload_string '.app_env')"
DEPLOYMENT_TYPE="$(payload_string '.deployment_type')"
IMAGE_REF="$(payload_string '.image_ref')"
IMAGE_TAG="$(payload_string '.image_tag')"
DOMAIN="$(payload_string '.domain')"
DATABASE="$(payload_string '.database')"
POSTGRES_PASSWORD="$(payload_string '.postgres_password')"
POSTGRES_PASSWORD_UPDATE="$(payload_string '.postgres_password_update')"
GHCR_USERNAME="$(payload_string '.ghcr_username')"
GHCR_TOKEN="$(payload_string '.ghcr_token')"
GITHUB_SHA="$(payload_string '.github_sha')"
DEPLOYMENT_ID="$(payload_string '.deployment_id')"
ARTIFACT_VOLUME_MOUNT_PATH="$(payload_string '.artifact_volume_mount_path')"
MINICLOUD_OPENBAO_UNSEAL_KEY="$(payload_string '.minicloud_openbao_unseal_key')"
MINICLOUD_OPENBAO_ROOT_TOKEN="$(payload_string '.minicloud_openbao_root_token')"
MINICLOUD_OPENBAO_API_TOKEN="$(payload_string '.minicloud_openbao_api_token')"
SERVICES_JSON="$(jq -c '.services // empty' "${PAYLOAD_FILE}")"
BUILD_ARTIFACTS_JSON="$(jq -c '.build_artifacts // empty' "${PAYLOAD_FILE}")"
MAX_PARALLEL_BUILDS="$(jq -r '.max_parallel_builds // 1' "${PAYLOAD_FILE}")"
MAX_PARALLEL_NETWORK_OPERATIONS="$(jq -r '.max_parallel_network_operations // 1' "${PAYLOAD_FILE}")"
CACHE_MAX_BYTES="$(jq -r '.build_cache.max_bytes // 4294967296' "${PAYLOAD_FILE}")"
CACHE_MAX_AGE_HOURS="$(jq -r '.build_cache.max_age_hours // 168' "${PAYLOAD_FILE}")"
CACHE_MIN_FREE_BYTES="$(jq -r '.build_cache.min_free_bytes // 2147483648' "${PAYLOAD_FILE}")"
CACHE_MIN_FREE_PERCENT="$(jq -r '.build_cache.min_free_percent // 10' "${PAYLOAD_FILE}")"
BUILD_PLATFORM=""
BUILD_BUILDER=""
DOCKER_ROOT=""

ROLLOUT_GATE_FILE="$(payload_string '.rollout_gate_file')"
OWNERSHIP_TOKEN="$(payload_string '.ownership_token')"
WORKER_LEASE_FILE="$(payload_string '.worker_lease_file')"
LEASE_WATCHDOG_PID=""

if [ -z "${DEPLOYMENT_ID}" ]; then
  DEPLOYMENT_ID="${GITHUB_SHA:-$(date +%s)}"
fi
if [ -z "${GITHUB_SHA}" ]; then
  GITHUB_SHA="${DEPLOYMENT_ID}"
fi

if [ -n "${BUILD_ARTIFACTS_JSON}" ] && [ "${BUILD_ARTIFACTS_JSON}" != "null" ]; then
  SERVICES_JSON="$(
    jq -cn \
      --argjson services "${SERVICES_JSON}" \
      --argjson artifacts "${BUILD_ARTIFACTS_JSON}" \
      --arg app "${APP_NAME}" \
      --arg dep "${DEPLOYMENT_ID}" '
        $services | map(
          . as $svc
          | ($artifacts | map(select((.service_name // .name) == $svc.name)) | first) as $match
          | if $match != null or ($svc.image // "") == "" then
              $svc + {image: ($match.image_tag // ("minicloud/" + $app + "-" + $svc.name + ":" + $dep))}
            else
              $svc
            end
        )
      '
  )"
fi

if [ -z "${ARTIFACT_VOLUME_MOUNT_PATH}" ]; then
  ARTIFACT_VOLUME_MOUNT_PATH="/var/lib/minicloud/artifacts"
fi

APP_DIR="/opt/minicloud/apps/${APP_NAME}"
LOCK_DIR="/opt/minicloud/locks"
CADDY_APP="/opt/minicloud/caddy/apps/${APP_NAME}.caddy"
CADDY_PREVIOUS="${APP_DIR}/shared/${APP_NAME}.caddy.previous"
CADDY_NEXT="${APP_DIR}/shared/${APP_NAME}.caddy.next"
INFRA_COMPOSE_FILE="${APP_DIR}/infra.compose.json"
APP_COMPOSE_FILE="${APP_DIR}/app.compose.json"
OBSERVABILITY_COMPOSE_FILE="${APP_DIR}/observability.compose.json"
OBSERVABILITY_CONFIG_DIR="${APP_DIR}/shared/observability"
# Separate provider-backed observability volume. Never share an unbounded log
# filesystem with artifacts, Postgres, or OpenBao. Skip observability when
# the expected mount is absent; never silently write to the root disk.
OBSERVABILITY_DIR="/var/lib/minicloud/observability"
# Pinned observability images with digests (T10). Keep in sync with
# compose.observability.yml and observability/README.md. Never use `latest`.
LOKI_IMAGE="grafana/loki:3.5.2@sha256:d0a95e651bb7d0a5a6468035c5b49c52f4678d90d598f853c06716bca102686b"
GRAFANA_IMAGE="grafana/grafana-oss:11.6.5@sha256:d552f949693c54d17c6f867ff6aeb128b021e54e923895dcf9cd6aa8176c0d74"
TELEMETRY_IMAGE="$(payload_string '.telemetry_image')"
if [ -z "${TELEMETRY_IMAGE}" ]; then
  TELEMETRY_IMAGE="ghcr.io/${GHCR_USERNAME:-muniventures}/minicloud-telemetry:${IMAGE_TAG:-latest}"
fi
# Container UIDs that own observability state (verified against the pinned
# images: `docker inspect <image> --format '{{.Config.User}}'`).
LOKI_DATA_UID=10001
LOKI_DATA_GID=10001
GRAFANA_DATA_UID=472
GRAFANA_DATA_GID=472
DEPLOYS_FILE="${APP_DIR}/deploys.json"
ENV_FILE="${APP_DIR}/shared/env"
SQLITE_DIR="${APP_DIR}/data/sqlite"
LEGACY_POSTGRES_DIR="${APP_DIR}/data/postgres"
POSTGRES_DIR="${LEGACY_POSTGRES_DIR}"
OPENBAO_MOUNT_PATH="minicloud-apps"
POSTGRES_DATA_UID=70
POSTGRES_DATA_GID=70
CURRENT_PHASE="startup"
CURRENT_PHASE_STARTED_AT="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
CURRENT_PHASE_STARTED_MS=""

monotonic_ms() {
  python3 -c 'import time; print(time.monotonic_ns() // 1000000)'
}

finish_phase() {
  local outcome="${1:-succeeded}"
  if [ -z "${CURRENT_PHASE_STARTED_MS}" ]; then
    return
  fi
  local finished_ms
  local duration_ms
  finished_ms="$(monotonic_ms)"
  duration_ms=$((finished_ms - CURRENT_PHASE_STARTED_MS))
  if [ "${duration_ms}" -lt 0 ]; then
    duration_ms=0
  fi
  echo "TIMING phase=${CURRENT_PHASE} deployment_id=${DEPLOYMENT_ID} started_at=${CURRENT_PHASE_STARTED_AT} duration_ms=${duration_ms} outcome=${outcome}"
}

if [ "${APP_ENV}" = "production" ]; then
  OPENBAO_ENVIRONMENT_PREFIX="prod"
else
  OPENBAO_ENVIRONMENT_PREFIX="${APP_ENV}"
fi

MINICLOUD_OPENBAO_ENABLED="false"
if [ "${CUSTOMER}" = "internal" ] && [ "${APP_NAME}" = "minicloud" ]; then
  MINICLOUD_OPENBAO_ENABLED="true"
fi

if [ "${MINICLOUD_OPENBAO_ENABLED}" = "true" ]; then
  OPENBAO_DIR="${ARTIFACT_VOLUME_MOUNT_PATH}/openbao"
  POSTGRES_DIR="${ARTIFACT_VOLUME_MOUNT_PATH}/postgres"
else
  OPENBAO_DIR="${APP_DIR}/data/openbao"
fi
OPENBAO_BOOTSTRAP_FILE="${OPENBAO_DIR}/bootstrap.json"
POSTGRES_DB="${APP_NAME//-/_}"

phase() {
  finish_phase succeeded
  CURRENT_PHASE="$1"
  CURRENT_PHASE_STARTED_AT="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  CURRENT_PHASE_STARTED_MS="$(monotonic_ms)"
  echo "===== phase: ${CURRENT_PHASE} ====="
}

compose() {
  local args=(-p "minicloud-${APP_NAME}")
  if [ -f "${INFRA_COMPOSE_FILE}" ]; then
    args+=(-f "${INFRA_COMPOSE_FILE}")
  fi
  args+=(-f "${APP_COMPOSE_FILE}")
  docker compose "${args[@]}" "$@"
}

compose_diagnostics() {
  echo "===== docker compose ps ====="
  if [ -f "${APP_COMPOSE_FILE}" ]; then
    compose ps || true
  else
    echo "Compose file ${APP_COMPOSE_FILE} does not exist."
  fi

  echo "===== docker compose logs ====="
  if [ -f "${APP_COMPOSE_FILE}" ]; then
    compose logs --tail=200 || true
  fi
}

caddy_diagnostics() {
  echo "===== caddy validate ====="
  sudo caddy validate --config /etc/caddy/Caddyfile || true
  echo "===== caddy status ====="
  sudo systemctl status caddy --no-pager || true
}

reload_caddy() {
  # Validate disk access as the service user before a privileged reload hides it.
  sudo -u caddy caddy validate --config /etc/caddy/Caddyfile || return 1
  sudo caddy reload --config /etc/caddy/Caddyfile || sudo systemctl restart caddy
}

has_external_ghcr_images() {
  if [ -n "${SERVICES_JSON:-}" ] && [ "${SERVICES_JSON}" != "null" ]; then
    if printf '%s' "${SERVICES_JSON}" | jq -e '
      [ .[] | select(.image | type == "string" and startswith("ghcr.io/")) ] | length > 0
    ' >/dev/null 2>&1; then
      return 0
    fi
  fi
  if [[ "${IMAGE_REF:-}" == ghcr.io/* ]]; then
    return 0
  fi
  return 1
}

on_error() {
  local status="$?"
  local line="${1:-unknown}"
  local failure_code="remote_deploy_failed"
  trap - ERR
  finish_phase failed
  if [ "${CURRENT_PHASE}" = "build_artifacts" ]; then
    failure_code="docker_build_failed"
  elif [ "${CURRENT_PHASE}" = "storage_mounts" ]; then
    failure_code="storage_mount_failed"
  elif [ "${CURRENT_PHASE}" = "runtime_agent_install" ]; then
    failure_code="runtime_agent_install_failed"
  elif [ "${CURRENT_PHASE}" = "verify_local_health" ]; then
    failure_code="health_check_failed"
  fi
  echo "__MINICLOUD_DEPLOY_FAILURE__=${failure_code}"
  echo "ERROR: ${failure_code}: Deployment aborted during phase ${CURRENT_PHASE} at line ${line}." >&2
  echo "Remote deploy failed during phase ${CURRENT_PHASE} at line ${line} with status ${status}."
  compose_diagnostics
  caddy_diagnostics
  exit "${status}"
}
trap 'on_error "$LINENO"' ERR

worker_lease_valid() {
  [ -n "${OWNERSHIP_TOKEN}" ] || return 0
  [ -n "${WORKER_LEASE_FILE}" ] && [ -f "${WORKER_LEASE_FILE}" ] || return 1
  [ "$(cat "${WORKER_LEASE_FILE}" 2>/dev/null || true)" = "${OWNERSHIP_TOKEN}" ] || return 1
  local modified now
  modified="$(stat -c %Y "${WORKER_LEASE_FILE}" 2>/dev/null || stat -f %m "${WORKER_LEASE_FILE}" 2>/dev/null)" || return 1
  now="$(date +%s)"
  [ $((now - modified)) -le 5 ]
}

start_worker_lease_watchdog() {
  [ -n "${OWNERSHIP_TOKEN}" ] || return 0
  worker_lease_valid || { echo "ERROR: worker_lease_expired: missing or invalid worker lease." >&2; exit 1; }
  (
    while sleep 1; do
      if ! worker_lease_valid; then
        echo "ERROR: worker_lease_expired: worker ownership lease expired." >&2
        kill -TERM "$$" 2>/dev/null || true
        exit 1
      fi
    done
  ) &
  LEASE_WATCHDOG_PID=$!
}

stop_worker_lease_watchdog() {
  [ -z "${LEASE_WATCHDOG_PID}" ] || kill "${LEASE_WATCHDOG_PID}" 2>/dev/null || true
  [ -z "${LEASE_WATCHDOG_PID}" ] || wait "${LEASE_WATCHDOG_PID}" 2>/dev/null || true
}

activate_caddy_with_lease_fence() {
  if [ -z "${OWNERSHIP_TOKEN}" ]; then
    activate_caddy
    return
  fi
  exec 8>"${WORKER_LEASE_FILE}.lock"
  flock -x 8
  worker_lease_valid || { flock -u 8; echo "ERROR: worker_lease_expired: refusing activation." >&2; exit 1; }
  activate_caddy
  if ! worker_lease_valid; then
    echo "ERROR: worker_lease_expired: lease changed during activation; restoring previous config." >&2
    if [ -f "${CADDY_PREVIOUS}" ]; then
      sudo install -m 0644 "${CADDY_PREVIOUS}" "${CADDY_APP}"
      sudo caddy validate --config /etc/caddy/Caddyfile || true
      reload_caddy || true
    else
      sudo rm -f "${CADDY_APP}"
      reload_caddy || true
    fi
    flock -u 8
    exit 1
  fi
  flock -u 8
}

preflight() {
  phase "preflight"
  case "$(uname -m)" in
    x86_64|amd64) echo '__MINICLOUD_BUILD_PLATFORM__=linux/amd64' ;;
    aarch64|arm64) echo '__MINICLOUD_BUILD_PLATFORM__=linux/arm64' ;;
  esac

  for command_name in jq docker curl sudo caddy flock; do
    if ! command -v "${command_name}" >/dev/null 2>&1; then
      echo "Required command ${command_name} is not installed."
      exit 1
    fi
  done

  if [ -z "${APP_NAME}" ] || [ -z "${CUSTOMER}" ] || [ -z "${APP_ENV}" ]; then
    echo "APP_NAME, CUSTOMER, and APP_ENV are required."
    exit 1
  fi

  if has_external_ghcr_images; then
    if [ -z "${GHCR_TOKEN}" ]; then
      echo "GHCR_READ_TOKEN environment secret is required for Minicloud-owned GHCR pulls."
      exit 1
    fi
  fi

  if [ "${DATABASE}" != "postgres" ] && [ "${DATABASE}" != "sqlite" ] && [ "${DATABASE}" != "none" ]; then
    echo "Unsupported database '${DATABASE}'."
    exit 1
  fi
  if [ "${DATABASE}" = "postgres" ] && [ -z "${POSTGRES_PASSWORD}" ]; then
    echo "Postgres password is required before deployment."
    exit 1
  fi

  if jq -e '.asset_publication != null' "${PAYLOAD_FILE}" >/dev/null; then return; fi
  if [ -z "${SERVICES_JSON}" ] || ! printf '%s' "${SERVICES_JSON}" | jq -e 'type == "array" and length > 0' >/dev/null; then
    echo "services payload must be a non-empty JSON array."
    exit 1
  fi

  if ! [[ "${MAX_PARALLEL_NETWORK_OPERATIONS}" =~ ^[1-8]$ ]]; then
    echo "max_parallel_network_operations must be between 1 and 8."
    exit 1
  fi

  if ! printf '%s' "${SERVICES_JSON}" | jq -e '
    all(.[]; (.name | type == "string" and test("^[A-Za-z0-9][A-Za-z0-9_.-]*$"))
      and (.image | type == "string" and length > 0)
      and (.port | type == "number"))
  ' >/dev/null; then
    echo "Each service requires a valid name, image, and numeric port."
    exit 1
  fi

  PUBLIC_SERVICE_COUNT="$(printf '%s' "${SERVICES_JSON}" | jq '[.[] | select(.public // false)] | length')"
  if [ "${PUBLIC_SERVICE_COUNT}" -eq 0 ]; then
    echo "At least one service must have public=true."
    exit 1
  fi

  if [ "${MINICLOUD_OPENBAO_ENABLED}" = "true" ]; then
    if [ -z "${MINICLOUD_OPENBAO_API_TOKEN}" ]; then
      echo "MINICLOUD_OPENBAO_API_TOKEN secret is required for Minicloud OpenBao."
      exit 1
    fi
    if ! mountpoint -q "${ARTIFACT_VOLUME_MOUNT_PATH}"; then
      echo "Minicloud OpenBao storage requires mounted volume at ${ARTIFACT_VOLUME_MOUNT_PATH}."
      exit 1
    fi
  fi
}

validate_cache_policy() {
  local value
  for value in "$CACHE_MAX_BYTES" "$CACHE_MAX_AGE_HOURS" "$CACHE_MIN_FREE_BYTES" "$CACHE_MIN_FREE_PERCENT"; do
    if ! [[ "$value" =~ ^[1-9][0-9]{0,11}$ ]]; then
      echo "ERROR: invalid_build_cache_policy: expected bounded positive integers." >&2
      return 1
    fi
  done
  if (( CACHE_MAX_BYTES < 268435456 || CACHE_MAX_BYTES > 17179869184 ||
        CACHE_MAX_AGE_HOURS > 720 || CACHE_MIN_FREE_BYTES < 268435456 ||
        CACHE_MIN_FREE_BYTES > 17179869184 || CACHE_MIN_FREE_PERCENT > 50 )); then
    echo "ERROR: invalid_build_cache_policy: cache/free bytes must be 256 MiB–16 GiB, age 1–720 hours, free percent 1–50." >&2
    return 1
  fi
}

prepare_builder() {
  phase "prepare_builder"
  validate_cache_policy
  if ! docker buildx version >/dev/null 2>&1; then
    echo "ERROR: buildkit_unavailable: install Ubuntu docker-buildx using the documented existing-server upgrade before retrying." >&2
    return 1
  fi
  BUILD_BUILDER="$(docker context show)"
  local driver architecture
  driver="$(docker buildx inspect --builder "$BUILD_BUILDER" | awk '/^Driver:/ {print $2}')"
  if [ "$driver" != docker ]; then
    echo "ERROR: buildkit_unavailable: Minicloud requires the local Docker driver." >&2
    return 1
  fi
  architecture="$(docker info --format '{{.Architecture}}')"
  case "$architecture" in
    x86_64|amd64) BUILD_PLATFORM=linux/amd64 ;;
    aarch64|arm64) BUILD_PLATFORM=linux/arm64 ;;
    *) echo "ERROR: buildkit_unavailable: unsupported runtime architecture." >&2; return 1 ;;
  esac
  DOCKER_ROOT="$(docker info --format '{{.DockerRootDir}}')"
  echo "TIMING phase=builder deployment_id=$DEPLOYMENT_ID builder=$BUILD_BUILDER platform=$BUILD_PLATFORM version=$(docker buildx version | awk '{print $2}')"
  maintain_build_cache
  require_build_disk
}

cache_usage_bytes() {
  docker buildx du --builder "$BUILD_BUILDER" | python3 -c '
import re, sys
match = re.search(r"^Total:\s*([0-9.]+)\s*([kKMGT]?i?B)", sys.stdin.read(), re.M)
if not match: sys.exit(1)
number, unit = match.groups()
power = {"B": 0, "K": 1, "M": 2, "G": 3, "T": 4}[unit[0].upper()]
print(int(float(number) * (1024 if "i" in unit else 1000) ** power))'
}

build_disk_free() {
  # statvfs accounts for images, containers, logs and volumes on the same filesystem.
  sudo -n python3 - "$DOCKER_ROOT" <<'PYDISK'
import os, sys
s = os.statvfs(sys.argv[1])
print(s.f_bavail * s.f_frsize, s.f_blocks * s.f_frsize)
PYDISK
}

require_build_disk() {
  local available total disk
  disk="$(build_disk_free)" || return 1
  read -r available total <<< "$disk"
  if (( available < CACHE_MIN_FREE_BYTES || available * 100 / total < CACHE_MIN_FREE_PERCENT )); then
    echo "ERROR: build_disk_capacity: insufficient Docker disk headroom; available_bytes=$available required_bytes=$CACHE_MIN_FREE_BYTES required_percent=$CACHE_MIN_FREE_PERCENT." >&2
    return 1
  fi
}

maintain_build_cache() {
  # The host Docker lock spans preparation, activation and cleanup across all apps.
  local before after available total reclaimed disk started
  started="$(monotonic_ms)"
  before="$(cache_usage_bytes)" || return 1
  docker builder prune --force --all --filter "until=${CACHE_MAX_AGE_HOURS}h" >/dev/null || return 1
  docker builder prune --force --all --keep-storage "$CACHE_MAX_BYTES" >/dev/null || return 1
  disk="$(build_disk_free)" || return 1
  read -r available total <<< "$disk"
  if (( available < CACHE_MIN_FREE_BYTES || available * 100 / total < CACHE_MIN_FREE_PERCENT )); then
    # Last resort: unused build cache only. Never delete containers, images or volumes.
    docker builder prune --force --all --keep-storage 0 >/dev/null || return 1
  fi
  after="$(cache_usage_bytes)" || return 1
  reclaimed=$((before > after ? before - after : 0))
  echo "TIMING phase=cache_retention deployment_id=$DEPLOYMENT_ID duration_ms=$(( $(monotonic_ms) - started )) before_bytes=$before after_bytes=$after reclaimed_bytes=$reclaimed max_bytes=$CACHE_MAX_BYTES max_age_hours=$CACHE_MAX_AGE_HOURS min_free_bytes=$CACHE_MIN_FREE_BYTES min_free_percent=$CACHE_MIN_FREE_PERCENT outcome=succeeded"
}

acquire_lock() {
  phase "acquire_lock"
  sudo mkdir -p "${LOCK_DIR}"
  sudo chown minicloud:minicloud "${LOCK_DIR}"
  # Serialize builds and host-wide cleanup on the same Docker daemon.
  exec 7>"${LOCK_DIR}/docker.deploy.lock"
  flock 7
  exec 9>"${LOCK_DIR}/${APP_NAME}.deploy.lock"
  flock 9
}

install_runtime_maintenance() {
  local helper
  helper="$(dirname "${PAYLOAD_FILE}")/runtime-maintenance.py"
  sudo install -d -m 0755 /opt/minicloud/bin /etc/systemd/journald.conf.d
  sudo install -o root -g root -m 0755 "$helper" /opt/minicloud/bin/runtime-maintenance.py
  printf '[Journal]\nMaxRetentionSec=2day\nMaxFileSec=1h\nSystemMaxUse=256M\nRuntimeMaxUse=64M\n' | sudo tee /etc/systemd/journald.conf.d/minicloud-retention.conf >/dev/null
  sudo tee /etc/systemd/system/minicloud-maintenance.service >/dev/null <<'MAINTENANCE_SERVICE'
[Unit]
Description=Minicloud disk and two-day log retention
After=docker.service systemd-journald.service
[Service]
Type=oneshot
ExecStart=/usr/bin/python3 /opt/minicloud/bin/runtime-maintenance.py
TimeoutStartSec=15min
Nice=10
MAINTENANCE_SERVICE
  sudo tee /etc/systemd/system/minicloud-maintenance.timer >/dev/null <<'MAINTENANCE_TIMER'
[Unit]
Description=Hourly Minicloud disk maintenance
[Timer]
OnBootSec=10min
OnUnitActiveSec=1h
Persistent=true
[Install]
WantedBy=timers.target
MAINTENANCE_TIMER
  sudo systemctl daemon-reload
  sudo systemctl restart systemd-journald
  sudo systemctl enable --now minicloud-maintenance.timer
  if ! python3 "$helper" --lock-held --files-only --preserve "${APP_DIR}/builds/${DEPLOYMENT_ID}" --preserve "$(dirname "${PAYLOAD_FILE}")"; then
    echo "WARNING: Pre-build directory cleanup failed; periodic maintenance will retry."
  fi
}

prepare_paths() {
  phase "prepare_paths"
  mkdir -p "${APP_DIR}/shared" "${OPENBAO_DIR}/config" "${OPENBAO_DIR}/data"
  if [ "${DATABASE}" = "sqlite" ]; then
    mkdir -p "${SQLITE_DIR}"
  fi
  if [ "${DATABASE}" = "postgres" ]; then
    sudo mkdir -p "${POSTGRES_DIR}"
    if [ ! -f "${POSTGRES_DIR}/PG_VERSION" ]; then
      sudo chown "${POSTGRES_DATA_UID}:${POSTGRES_DATA_GID}" "${POSTGRES_DIR}"
    fi
  fi
  if [ "${MINICLOUD_OPENBAO_ENABLED}" = "true" ]; then
    sudo chown -R 100:1000 "${OPENBAO_DIR}/data"
  fi
  printf 'import /opt/minicloud/caddy/apps/*.caddy\n' | sudo tee /etc/caddy/Caddyfile >/dev/null
  touch "${ENV_FILE}"
  chmod 600 "${ENV_FILE}"
}

normalize_services() {
  SERVICES_JSON="$(
    printf '%s' "${SERVICES_JSON}" | jq -c '
      to_entries
      | map(.value + {host_port: (.value.host_port // (18080 + .key))})
    '
  )"
}

prepare_service_paths() {
  local artifact_paths
  artifact_paths="$(
    printf '%s' "${SERVICES_JSON}" | jq -r --arg customer "${CUSTOMER}" --arg app "${APP_NAME}" '
      .[]
      | select($customer == "internal" and $app == "minicloud" and (.name == "backend" or .name == "worker"))
      | .env.DeploymentArtifacts__RootPath // empty
    '
  )"

  while IFS= read -r artifact_path; do
    if [ -z "${artifact_path}" ]; then
      continue
    fi
    if [[ "${artifact_path}" != /* ]]; then
      echo "DeploymentArtifacts__RootPath must be an absolute path."
      exit 1
    fi
    sudo mkdir -p "${artifact_path}"
  done <<< "${artifact_paths}"
}

prepare_minicloud_registry() {
  if [ "${DEPLOYMENT_TYPE}" != "custom" ] || [ "${CUSTOMER}" != "internal" ] || [ "${APP_NAME}" != "minicloud" ] || ! printf '%s' "${SERVICES_JSON}" | jq -e 'any(.[]; .name == "registry")' >/dev/null; then return; fi
  if ! mountpoint -q "${ARTIFACT_VOLUME_MOUNT_PATH}"; then
    echo 'ERROR: registry_storage_missing: Registry requires the mounted provider storage volume.' >&2; return 1
  fi
  local auth_root="${ARTIFACT_VOLUME_MOUNT_PATH}/registry-auth"
  sudo install -d -m 700 -o minicloud -g minicloud "${auth_root}" "${ARTIFACT_VOLUME_MOUNT_PATH}/registry-backups"
  sudo install -d -m 755 -o 1000 -g 1000 "${ARTIFACT_VOLUME_MOUNT_PATH}/registry"
  if [ ! -f "${auth_root}/key.pem" ] && [ ! -f "${auth_root}/cert.pem" ]; then
    (umask 077; openssl req -x509 -newkey rsa:3072 -nodes -days 1095 -subj /CN=minicloud-registry \
      -addext basicConstraints=critical,CA:TRUE -addext keyUsage=critical,keyCertSign,digitalSignature \
      -keyout "${auth_root}/key.pem" -out "${auth_root}/cert.pem" >/dev/null 2>&1)
    chmod 644 "${auth_root}/cert.pem"
  fi
  openssl x509 -in "${auth_root}/cert.pem" -checkend 86400 -noout >/dev/null
  if [ ! -f "${auth_root}/http-secret" ]; then (umask 077; openssl rand -hex 32 > "${auth_root}/http-secret"); fi
  REGISTRY_HTTP_SECRET="$(cat "${auth_root}/http-secret")"
  export REGISTRY_HTTP_SECRET
  local helper="$(dirname "${PAYLOAD_FILE}")/registry-maintenance.sh"
  if [ ! -f "${helper}" ]; then echo 'Registry maintenance helper is missing.' >&2; return 1; fi
  sudo install -m 0755 "${helper}" /opt/minicloud/registry-maintenance
  sudo tee /etc/systemd/system/minicloud-registry-maintenance.service >/dev/null <<EOF
[Unit]
Description=Minicloud private registry backup and garbage collection
After=docker.service
Requires=docker.service
StartLimitIntervalSec=3600
StartLimitBurst=3
[Service]
Type=oneshot
User=minicloud
Group=minicloud
SupplementaryGroups=docker
ExecStart=/opt/minicloud/registry-maintenance ${APP_DIR} gc
TimeoutStartSec=1800
Nice=10
IOSchedulingClass=idle
PrivateTmp=true
NoNewPrivileges=true
ProtectSystem=strict
ReadWritePaths=${ARTIFACT_VOLUME_MOUNT_PATH} /opt/minicloud/locks
Restart=on-failure
RestartSec=60
EOF
  sudo tee /etc/systemd/system/minicloud-registry-maintenance.timer >/dev/null <<'EOF'
[Unit]
Description=Hourly Minicloud private registry maintenance
[Timer]
OnCalendar=hourly
RandomizedDelaySec=5m
Persistent=true
[Install]
WantedBy=timers.target
EOF
  sudo chmod 0644 /etc/systemd/system/minicloud-registry-maintenance.service /etc/systemd/system/minicloud-registry-maintenance.timer
  sudo systemctl daemon-reload
  sudo systemctl enable --now minicloud-registry-maintenance.timer >/dev/null
}

render_next_configs() {
  phase "render_next_configs"
  normalize_services
  prepare_service_paths

  jq -n \
    --argjson services "${SERVICES_JSON}" \
    --arg customer "${CUSTOMER}" \
    --arg registry_root "${ARTIFACT_VOLUME_MOUNT_PATH}" \
    --arg deployment_type "${DEPLOYMENT_TYPE}" \
    --arg app "${APP_NAME}" \
    --arg database "${DATABASE}" \
    --arg env_file "${ENV_FILE}" \
    --arg sqlite_dir "${SQLITE_DIR}" \
    --arg postgresql_connection_string "Host=db;Database=${POSTGRES_DB};Username=minicloud;Password=${POSTGRES_PASSWORD}" \
    --arg openbao_enabled "${MINICLOUD_OPENBAO_ENABLED}" \
    --arg openbao_mount_path "${OPENBAO_MOUNT_PATH}" \
    --arg openbao_environment_prefix "${OPENBAO_ENVIRONMENT_PREFIX}" \
    --arg openbao_api_token "${MINICLOUD_OPENBAO_API_TOKEN}" '
      def service_environment($service):
        if $deployment_type == "custom" and $customer == "internal" and $app == "minicloud" and $service.name == "registry" then
          ($service.env // {}) + {REGISTRY_HTTP_SECRET: (env.REGISTRY_HTTP_SECRET // "")}
        else
        ($service.env // {})
        + (if $database == "sqlite" then
            {DATABASE_URL: "sqlite:////data/sqlite/app.db"}
          elif $database == "postgres" then
            {
              DATABASE_URL: $postgresql_connection_string,
              ConnectionStrings__Minicloud: $postgresql_connection_string
            }
          else
            {}
          end)
        + (if $openbao_enabled == "true" and ($service.name == "backend" or $service.name == "worker") then
            {
              OpenBao__Address: "http://openbao:8200",
              OpenBao__Token: $openbao_api_token,
              OpenBao__MountPath: $openbao_mount_path,
              OpenBao__EnvironmentPrefix: $openbao_environment_prefix,
              Grafana__OperatorSecretPath: ("env/" + $openbao_environment_prefix + "/ops/grafana/server-admin")
            }
          else
            {}
          end) end;

      def service_volumes($service):
        (($service.storage_mounts // []) | map({type: "bind", source: ("/var/lib/minicloud/customer-storage/" + .volume_id + "/data"), target: .mount_path, bind: {create_host_path: false}}))
        +
        (($service.asset_mounts // []) | map({type: "bind", source: ("/opt/minicloud/apps/" + $app + "/assets/" + .name + (if .release_id then "/" + .release_id else "" end)), target: .mount_path, read_only: true, bind: {create_host_path: false}}))
        + (
          if $database == "sqlite" then
            [$sqlite_dir + ":/data/sqlite"]
          else
            []
          end
        )
        + (
          if $customer == "internal" and $app == "minicloud" and ($service.name == "backend" or $service.name == "worker") and (($service.env.DeploymentArtifacts__RootPath // "") != "") then
            [($service.env.DeploymentArtifacts__RootPath + ":" + $service.env.DeploymentArtifacts__RootPath)]
          else
            []
          end
        )
        + (if $deployment_type == "custom" and $customer == "internal" and $app == "minicloud" and $service.name == "registry" then
            [ {type: "bind", source: ($registry_root + "/registry"), target: "/var/lib/registry", bind: {create_host_path: false}},
              {type: "bind", source: ($registry_root + "/registry-auth/cert.pem"), target: "/etc/registry/cert.pem", read_only: true, bind: {create_host_path: false}} ]
          else [] end);

      # App services deliberately never depend on loki/grafana: the API must
      # still start with application telemetry disabled or unavailable (T10).
      def service_depends_on($service):
        {}
        + (if $database == "postgres" then {db: {condition: "service_healthy"}} else {} end)
        + (if $openbao_enabled == "true" and $service.name == "backend" then {openbao: {condition: "service_healthy"}} else {} end);

      {
        services: (
          reduce $services[] as $service ({};
            .[$service.name] = (
              {
                image: $service.image,
                restart: "unless-stopped",
                logging: {driver: "journald", options: {tag: ("minicloud/" + $app + "/" + $service.name)}},
                env_file: [$env_file],
                environment: service_environment($service)
              }
              + (if ($service.public // false) then {ports: ["127.0.0.1:\($service.host_port):\($service.port)"]} else {} end)
              + (if (service_volumes($service) | length) > 0 then {volumes: service_volumes($service)} else {} end)
              + (if (service_depends_on($service) | length) > 0 then {depends_on: service_depends_on($service)} else {} end)
              + (if $deployment_type == "custom" and $customer == "internal" and $app == "minicloud" and $service.name == "registry" then
                  {env_file: [], user: "1000:1000", mem_limit: "512m", cpus: 1, pids_limit: 128, read_only: true,
                   cap_drop: ["ALL"], security_opt: ["no-new-privileges:true"], tmpfs: ["/tmp:rw,nosuid,noexec,size=16m"]}
                 else {} end)
            )
          )
        )
      }
    ' > "${APP_COMPOSE_FILE}"

  jq -n \
    --arg database "${DATABASE}" \
    --arg postgres_password "${POSTGRES_PASSWORD}" \
    --arg postgres_db "${POSTGRES_DB}" \
    --arg postgres_dir "${POSTGRES_DIR}" \
    --arg openbao_enabled "${MINICLOUD_OPENBAO_ENABLED}" \
    --arg openbao_dir "${OPENBAO_DIR}" '
      {services: {}}
      | if $openbao_enabled == "true" then
          .services.openbao = {
            image: "openbao/openbao:latest",
            restart: "unless-stopped",
            cap_add: ["IPC_LOCK"],
            command: "server",
            volumes: [
              ($openbao_dir + "/config:/openbao/config"),
              ($openbao_dir + "/data:/openbao/data")
            ],
            healthcheck: {
              test: ["CMD", "bao", "status", "-address=http://127.0.0.1:8200"],
              interval: "5s",
              timeout: "5s",
              retries: 20
            }
          }
        else . end
      | if $database == "postgres" then
          .services.db = {
            image: "postgres:16-alpine",
            restart: "unless-stopped",
            environment: {
              POSTGRES_USER: "minicloud",
              POSTGRES_PASSWORD: $postgres_password,
              POSTGRES_DB: $postgres_db
            },
            volumes: [($postgres_dir + ":/var/lib/postgresql/data")],
            healthcheck: {
              test: ["CMD-SHELL", ("pg_isready -U minicloud -d " + $postgres_db)],
              interval: "5s",
              timeout: "5s",
              retries: 20
            }
          }
        else . end
    ' > "${INFRA_COMPOSE_FILE}"
  chmod 600 "${APP_COMPOSE_FILE}" "${INFRA_COMPOSE_FILE}"

  if [ "$(jq '.services | length' "${INFRA_COMPOSE_FILE}")" -eq 0 ]; then
    rm -f "${INFRA_COMPOSE_FILE}"
  fi

  {
    printf '%s' "${SERVICES_JSON}" | jq -c --arg customer "${CUSTOMER}" --arg app "${APP_NAME}" --arg default_host "${DOMAIN}" '
      [.[] | select(.public // false)
        | . as $service
        | (.hosts // [(.host // (if $default_host != "" then $default_host else "\($customer)-\($app)-\(.name).app.muni.dev" end))])[]
        | $service + {resolved_host: .}]
      | sort_by(.resolved_host, .name)[]
    ' | while read -r service; do
      HOSTNAME="$(printf '%s' "${service}" | jq -r '.resolved_host')"
      SERVICE_HOST_PORT="$(printf '%s' "${service}" | jq -r '.host_port')"
      echo "${HOSTNAME} {"
      echo "  reverse_proxy 127.0.0.1:${SERVICE_HOST_PORT}"
      echo "}"
    done
  } > "${CADDY_NEXT}"
}

validate_next_configs() {
  phase "validate_next_configs"
  # Only required app/infra config can block the rollout.
  compose config >/dev/null

  local caddy_validate_dir
  caddy_validate_dir="$(mktemp -d "${APP_DIR}/shared/caddy-validate.XXXXXX")"
  find /opt/minicloud/caddy/apps -maxdepth 1 -type f -name '*.caddy' ! -name "${APP_NAME}.caddy" -exec cp {} "${caddy_validate_dir}/" \; || true
  cp "${CADDY_NEXT}" "${caddy_validate_dir}/${APP_NAME}.caddy"
  printf 'import %s/*.caddy\n' "${caddy_validate_dir}" > "${caddy_validate_dir}/Caddyfile"
  sudo caddy validate --config "${caddy_validate_dir}/Caddyfile"
  rm -rf "${caddy_validate_dir}"
}

build_artifacts() {
  if [ -z "${BUILD_ARTIFACTS_JSON:-}" ] || [ "${BUILD_ARTIFACTS_JSON}" = "null" ]; then
    return
  fi

  local artifact_count
  artifact_count="$(printf '%s' "${BUILD_ARTIFACTS_JSON}" | jq 'length')"
  if [ "${artifact_count}" -eq 0 ]; then
    return
  fi

  phase "build_artifacts"
  local effective_build_concurrency=$((MAX_PARALLEL_BUILDS < artifact_count ? MAX_PARALLEL_BUILDS : artifact_count))
  echo "TIMING phase=build_artifacts deployment_id=${DEPLOYMENT_ID} artifact_count=${artifact_count} configured_concurrency=${MAX_PARALLEL_BUILDS} effective_concurrency=${effective_build_concurrency}"

  local builds_base_dir="${APP_DIR}/builds/${DEPLOYMENT_ID}"
  mkdir -p "${builds_base_dir}/artifacts" "${builds_base_dir}/src"

  if ! [[ "${MAX_PARALLEL_BUILDS}" =~ ^[1-4]$ ]]; then
    echo "ERROR: docker_build_failed: max_parallel_builds must be between 1 and 4." >&2
    exit 1
  fi

  local -a build_pids=()
  local running=0
  set -m

  stop_builds() {
    local pid attempt survivors=0
    # Signal every build process group first so slow siblings cannot delay
    # cancellation of later entries in the pool.
    for pid in "${build_pids[@]}"; do
      kill -TERM -- "-${pid}" 2>/dev/null || kill -TERM "${pid}" 2>/dev/null || true
    done

    for attempt in $(seq 1 50); do
      survivors=0
      for pid in "${build_pids[@]}"; do
        if kill -0 -- "-${pid}" 2>/dev/null; then
          survivors=$((survivors + 1))
        fi
      done
      [ "${survivors}" -eq 0 ] && break
      sleep 0.1
    done

    for pid in "${build_pids[@]}"; do
      if kill -0 -- "-${pid}" 2>/dev/null; then
        kill -KILL -- "-${pid}" 2>/dev/null || true
      fi
    done

    for attempt in $(seq 1 20); do
      survivors=0
      for pid in "${build_pids[@]}"; do
        if kill -0 -- "-${pid}" 2>/dev/null; then
          survivors=$((survivors + 1))
        fi
      done
      [ "${survivors}" -eq 0 ] && break
      sleep 0.1
    done

    for pid in "${build_pids[@]}"; do
      wait "${pid}" 2>/dev/null || true
      if kill -0 -- "-${pid}" 2>/dev/null; then
        echo "ERROR: docker_build_failed: Build process group ${pid} did not stop after SIGKILL." >&2
        return 1
      fi
    done
  }
  trap 'stop_builds || true; exit 143' TERM
  trap 'stop_builds || true; exit 130' INT

  reap_one_build() {
    local slot pid
    while true; do
      for slot in "${!build_pids[@]}"; do
        pid="${build_pids[${slot}]}"
        if ! kill -0 "${pid}" 2>/dev/null; then
          if ! wait "${pid}"; then
            return 1
          fi
          unset 'build_pids[slot]'
          return 0
        fi
      done
      sleep 0.1
    done
  }

  for index in $(seq 0 "$((artifact_count - 1))"); do
    (
    local artifact
    artifact="$(printf '%s' "${BUILD_ARTIFACTS_JSON}" | jq -c --argjson index "${index}" '.[$index]')"

    local service_name
    service_name="$(printf '%s' "${artifact}" | jq -r '.service_name // .name // empty')"
    if [ -z "${service_name}" ]; then
      echo "ERROR: docker_build_failed: Artifact entry at index ${index} is missing service_name." >&2
      exit 1
    fi

    local archive_path
    archive_path="$(printf '%s' "${artifact}" | jq -r '.artifact_archive // .archive_path // empty')"
    if [ -z "${archive_path}" ]; then
      archive_path="${builds_base_dir}/artifacts/${service_name}.zip"
    fi

    if [ ! -f "${archive_path}" ]; then
      echo "ERROR: docker_build_failed: Artifact archive not found at ${archive_path} for service ${service_name}." >&2
      exit 1
    fi
    local artifact_bytes
    artifact_bytes="$(wc -c < "${archive_path}" | tr -d '[:space:]')"
    local service_started_at service_started_ms
    service_started_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    service_started_ms="$(monotonic_ms)"

    local target_archive="${builds_base_dir}/artifacts/${service_name}.zip"
    if [ "${archive_path}" != "${target_archive}" ]; then
      cp "${archive_path}" "${target_archive}"
    fi

    local extract_dir="${builds_base_dir}/src/${service_name}"
    rm -rf "${extract_dir}"
    mkdir -p "${extract_dir}"

    echo "Extracting artifact for service ${service_name} into ${extract_dir}..."
    if ! python3 - "${archive_path}" "${extract_dir}" <<'PY'
import os
import stat
import sys
import zipfile
from pathlib import Path

archive_path = sys.argv[1]
extract_dir = os.path.abspath(sys.argv[2])
os.makedirs(extract_dir, exist_ok=True)

with zipfile.ZipFile(archive_path) as archive:
    for entry in archive.infolist():
        name = entry.filename.replace("\\", "/")
        if any(part == '..' for part in Path(name).parts):
            sys.exit(f"Unsafe zip entry path (path traversal): {entry.filename}")
        if name.startswith("/") or name.startswith("../") or "/../" in name:
            sys.exit(f"Unsafe zip entry path: {entry.filename}")
        mode = entry.external_attr >> 16
        if stat.S_ISLNK(mode):
            sys.exit(f"Symlink zip entries are rejected: {entry.filename}")
        target = os.path.abspath(os.path.join(extract_dir, name))
        if target != extract_dir and not target.startswith(extract_dir + os.sep):
            sys.exit(f"Unsafe zip entry target: {entry.filename}")
    archive.extractall(extract_dir)

for root, dirs, files in os.walk(extract_dir, followlinks=False):
    for item in dirs + files:
        full_path = os.path.join(root, item)
        if os.path.islink(full_path):
            sys.exit(f"Symlink rejected in extracted files: {item}")

# Payloads and environment files stay private, but copied source must remain
# readable by non-root image users. The containing deployment tree is private.
for root, dirs, files in os.walk(extract_dir, followlinks=False):
    os.chmod(root, 0o755)
    for name in files:
        os.chmod(os.path.join(root, name), 0o644)
with zipfile.ZipFile(archive_path) as archive:
    for entry in archive.infolist():
        if not entry.is_dir():
            target = os.path.join(extract_dir, entry.filename.replace("\\", "/"))
            os.chmod(target, 0o644 | ((entry.external_attr >> 16) & 0o111))
PY
    then
      echo "ERROR: docker_build_failed: Failed to extract artifact archive safely for service ${service_name}." >&2
      exit 1
    fi

    local image_tag
    image_tag="$(printf '%s' "${artifact}" | jq -r '.image_tag // empty')"
    if [ -z "${image_tag}" ]; then
      image_tag="minicloud/${APP_NAME}-${service_name}:${DEPLOYMENT_ID}"
    fi

    local manifest_path="${extract_dir}/minicloud-artifact-manifest.json"
    local dockerfile_rel=""
    local build_context_rel=""
    local -a build_args=()

    dockerfile_rel="$(printf '%s' "${artifact}" | jq -r '.dockerfile_path // empty')"
    build_context_rel="$(printf '%s' "${artifact}" | jq -r '.build_context // empty')"

    if [ -z "${dockerfile_rel}" ] && [ -f "${manifest_path}" ]; then
      dockerfile_rel="$(jq -r '.dockerfilePath // empty' "${manifest_path}")"
    fi
    if [ -z "${build_context_rel}" ] && [ -f "${manifest_path}" ]; then
      build_context_rel="$(jq -r '.buildContextPath // empty' "${manifest_path}")"
    fi

    if [ -f "${manifest_path}" ]; then
      if ! jq -e '
        (.buildEnv // {}) as $env
        | ($env | type) == "object"
          and all($env | to_entries[]; (.key | test("^[A-Za-z_][A-Za-z0-9_]*$")) and (.key | startswith("BUILDKIT_") | not) and (.value | type) == "string")
      ' "${manifest_path}" >/dev/null; then
        echo "ERROR: docker_build_failed: Artifact buildEnv is invalid for service ${service_name}." >&2
        exit 1
      fi

      while IFS= read -r encoded_entry; do
        [ -n "${encoded_entry}" ] || continue
        local entry_json
        local build_key
        local build_value
        entry_json="$(printf '%s' "${encoded_entry}" | base64 --decode)"
        build_key="$(printf '%s' "${entry_json}" | jq -r '.key')"
        build_value="$(printf '%s' "${entry_json}" | jq -r '.value')"
        build_args+=(--build-arg "${build_key}=${build_value}")
      done < <(jq -r '(.buildEnv // {}) | to_entries[] | @base64' "${manifest_path}")
    fi

    if [ -z "${dockerfile_rel}" ]; then
      dockerfile_rel="Dockerfile"
    fi
    if [ -z "${build_context_rel}" ]; then
      build_context_rel="."
    fi

    if [ -f "${manifest_path}" ]; then
      cp "${manifest_path}" "${builds_base_dir}/artifacts/${service_name}.manifest.json"
      rm -f "${manifest_path}"
    fi

    local build_context_abs
    if ! build_context_abs="$(python3 - "${extract_dir}" "${build_context_rel}" <<'PY'
import os, sys
root = os.path.abspath(sys.argv[1])
path = os.path.abspath(os.path.join(root, sys.argv[2]))
if path != root and not path.startswith(root + os.sep):
    sys.exit(1)
print(path)
PY
)"; then
      echo "ERROR: docker_build_failed: Build context escapes artifact root for service ${service_name}." >&2
      exit 1
    fi

    local dockerfile_abs
    if ! dockerfile_abs="$(python3 - "${extract_dir}" "${dockerfile_rel}" <<'PY'
import os, sys
root = os.path.abspath(sys.argv[1])
path = os.path.abspath(os.path.join(root, sys.argv[2]))
if path != root and not path.startswith(root + os.sep):
    sys.exit(1)
print(path)
PY
)"; then
      echo "ERROR: docker_build_failed: Dockerfile path escapes artifact root for service ${service_name}." >&2
      exit 1
    fi

    if [ ! -f "${dockerfile_abs}" ]; then
      echo "ERROR: docker_build_failed: Dockerfile not found at ${dockerfile_abs} for service ${service_name}." >&2
      exit 1
    fi

    echo "Building Docker image ${image_tag} for service ${service_name}..."
    local build_output_file="${builds_base_dir}/artifacts/${service_name}.build.log"
    if ! docker buildx build --builder "$BUILD_BUILDER" --platform "$BUILD_PLATFORM" \
      --load --progress plain \
      --build-arg "BUILDKIT_CACHE_MOUNT_NS=${APP_NAME}-${service_name}-${BUILD_PLATFORM//\//-}" \
      --tag "${image_tag}" --file "${dockerfile_abs}" \
      ${build_args[@]+"${build_args[@]}"} "${build_context_abs}" 2>&1 | tee "${build_output_file}" | while IFS= read -r line || [ -n "${line}" ]; do
        printf '[build:%s] %s\n' "${service_name}" "${line}"
    done; then
      rm -f "${build_output_file}"
      echo "ERROR: docker_build_failed: Docker build failed for service ${service_name} (image: ${image_tag})." >&2
      exit 1
    fi
    local service_finished_ms service_duration_ms cache_hit_lines
    service_finished_ms="$(monotonic_ms)"
    service_duration_ms=$((service_finished_ms - service_started_ms))
    if [ "${service_duration_ms}" -lt 0 ]; then
      service_duration_ms=0
    fi
    cache_hit_lines="$(grep -Ec '(^|[[:space:]])CACHED($|[[:space:]])|Using cache' "${build_output_file}" || true)"
    rm -f "${build_output_file}"
    echo "TIMING phase=service_build deployment_id=${DEPLOYMENT_ID} service=${service_name} started_at=${service_started_at} duration_ms=${service_duration_ms} artifact_bytes=${artifact_bytes} cache_hit_lines=${cache_hit_lines} outcome=succeeded"
    ) &
    build_pids+=("$!")
    running=$((running + 1))
    if [ "${running}" -ge "${MAX_PARALLEL_BUILDS}" ]; then
      if ! reap_one_build; then
        stop_builds
        echo "ERROR: docker_build_failed: A service build failed; sibling builds were stopped." >&2
        exit 1
      fi
      running=$((running - 1))
    fi
  done

  while [ "${running}" -gt 0 ]; do
    if ! reap_one_build; then
      stop_builds
      echo "ERROR: docker_build_failed: A service build failed; sibling builds were stopped." >&2
      exit 1
    fi
    running=$((running - 1))
  done
  trap - TERM INT
  set +m
}

verify_retained_images() {
  local candidate_images
  candidate_images="$(printf '%s' "${BUILD_ARTIFACTS_JSON:-[]}" | jq -r '.[].image_tag // empty')"

  while IFS= read -r image; do
    if [[ "${image}" == minicloud/* ]] && ! printf '%s\n' "${candidate_images}" | grep -Fqx -- "${image}"; then
      if ! docker image inspect "${image}" >/dev/null 2>&1; then
        echo "ERROR: docker_build_failed: Retained local image ${image} is missing; redeploy that service explicitly." >&2
        exit 1
      fi
    fi
  done < <(printf '%s' "${SERVICES_JSON}" | jq -r '.[].image // empty')
}

prepare_managed_images() (
  local count
  count="$(jq '.managed_images // [] | length' "${PAYLOAD_FILE}")"
  if [ "${count}" -eq 0 ]; then return; fi
  phase "pull_images"
  local platform auth_dir
  case "$(uname -m)" in
    x86_64|amd64) platform=linux/amd64 ;;
    aarch64|arm64) platform=linux/arm64 ;;
    *) echo 'ERROR: image_platform_mismatch: Unsupported host architecture.' >&2; return 1 ;;
  esac
  auth_dir="$(mktemp -d "${APP_DIR}/registry-auth.XXXXXX")"
  chmod 700 "${auth_dir}"
  trap 'rm -rf "${auth_dir}"' EXIT
  trap 'exit 143' TERM INT
  local index name image expected registry username
  for index in $(seq 0 "$((count - 1))"); do
    worker_lease_valid || { echo 'ERROR: worker_lease_expired: Registry preparation ownership lost.' >&2; return 1; }
    name="$(jq -r --argjson i "${index}" '.managed_images[$i].name' "${PAYLOAD_FILE}")"
    image="$(jq -r --argjson i "${index}" '.managed_images[$i].image' "${PAYLOAD_FILE}")"
    expected="$(jq -r --argjson i "${index}" '.managed_images[$i].platform' "${PAYLOAD_FILE}")"
    registry="$(jq -r --argjson i "${index}" '.managed_images[$i].registry' "${PAYLOAD_FILE}")"
    username="$(jq -r --argjson i "${index}" '.managed_images[$i].username' "${PAYLOAD_FILE}")"
    if [ "${platform}" != "${expected}" ] || ! [[ "${image}" =~ ^[a-z0-9.:-]+/org/[a-z0-9_-]+/app/[a-z0-9_-]+/service/[a-z0-9_.-]+@sha256:[a-f0-9]{64}$ ]] || [[ "${image}" != "${registry}/"* ]]; then
      echo 'ERROR: image_platform_mismatch: Invalid managed image target.' >&2; return 1
    fi
    if [ "$(docker image inspect "${image}" --format '{{.Os}}/{{.Architecture}}' 2>/dev/null || true)" = "${platform}" ] && docker image inspect "${image}" --format '{{range .RepoDigests}}{{println .}}{{end}}' 2>/dev/null | grep -Fqx -- "${image}"; then
      echo "[pull:${name}] Reusing verified ${platform} image already present by digest."
      continue
    fi
    echo "[pull:${name}] Pulling verified ${platform} image by digest."
    jq -r --argjson i "${index}" '.managed_images[$i].password' "${PAYLOAD_FILE}" | docker --config "${auth_dir}" login "${registry}" --username "${username}" --password-stdin >/dev/null
    timeout 900 docker --config "${auth_dir}" pull --platform "${platform}" "${image}"
    if [ "$(docker image inspect "${image}" --format '{{.Os}}/{{.Architecture}}')" != "${platform}" ] || ! docker image inspect "${image}" --format '{{range .RepoDigests}}{{println .}}{{end}}' | grep -Fqx -- "${image}"; then
      echo 'ERROR: image_incomplete: Pulled image identity did not match its admitted digest/platform.' >&2; return 1
    fi
    rm -f "${auth_dir}/config.json"
  done
)

wait_for_rollout_gate() {
  if [ -z "${ROLLOUT_GATE_FILE}" ]; then
    return
  fi

  phase "wait_for_rollout_gate"
  local deadline=$((SECONDS + 900))
  while [ ! -f "${ROLLOUT_GATE_FILE}" ]; do
    if [ "${SECONDS}" -ge "${deadline}" ]; then
      echo "ERROR: rollout_gate_timeout: DNS readiness gate was not released." >&2
      exit 1
    fi
    sleep 1
  done
  rm -f "${ROLLOUT_GATE_FILE}"
}

migrate_postgres_if_needed() {
  phase "migrate_postgres_if_needed"
  if [ "${DATABASE}" = "postgres" ] && [ "${POSTGRES_DIR}" != "${LEGACY_POSTGRES_DIR}" ]; then
    if [ ! -f "${POSTGRES_DIR}/PG_VERSION" ] && [ -f "${LEGACY_POSTGRES_DIR}/PG_VERSION" ]; then
      echo "Migrating Postgres data from ${LEGACY_POSTGRES_DIR} to ${POSTGRES_DIR}."
      if [ -f "${APP_DIR}/compose.yml" ]; then
        docker compose -p "minicloud-${APP_NAME}" -f "${APP_DIR}/compose.yml" stop db || true
      fi
      compose stop db || true
      sudo cp -a "${LEGACY_POSTGRES_DIR}/." "${POSTGRES_DIR}/"
    fi
  fi
}

postgres_data_needs_owner_repair() {
  if [ "${DATABASE}" != "postgres" ] || [ ! -d "${POSTGRES_DIR}" ]; then
    return 1
  fi

  if [ -f "${POSTGRES_DIR}/PG_VERSION" ]; then
    sudo find "${POSTGRES_DIR}" -maxdepth 2 \( ! -user "${POSTGRES_DATA_UID}" -o ! -group "${POSTGRES_DATA_GID}" \) -print -quit | grep -q .
    return
  fi

  [ "$(sudo stat -c '%u:%g' "${POSTGRES_DIR}")" != "${POSTGRES_DATA_UID}:${POSTGRES_DATA_GID}" ]
}

ensure_postgres_data_owner() {
  phase "ensure_postgres_data_owner"
  if [ "${DATABASE}" != "postgres" ]; then
    return
  fi

  sudo mkdir -p "${POSTGRES_DIR}"
  if ! postgres_data_needs_owner_repair; then
    return
  fi

  if [ -f "${POSTGRES_DIR}/PG_VERSION" ]; then
    echo "Repairing Postgres data ownership for ${POSTGRES_DIR}."
    compose stop db || true
    sudo chown -R "${POSTGRES_DATA_UID}:${POSTGRES_DATA_GID}" "${POSTGRES_DIR}"
  else
    echo "Setting empty Postgres data directory ownership for ${POSTGRES_DIR}."
    sudo chown "${POSTGRES_DATA_UID}:${POSTGRES_DATA_GID}" "${POSTGRES_DIR}"
  fi
}

assert_observability_dir_owner() {
  local dir="$1"
  local expected_owner="$2"
  local label="$3"

  sudo mkdir -p "${dir}"
  if [ -z "$(sudo ls -A "${dir}")" ]; then
    echo "Setting empty observability ${label} directory ownership for ${dir}."
    sudo chown "${expected_owner}" "${dir}"
    return
  fi

  local actual_owner
  actual_owner="$(sudo stat -c '%u:%g' "${dir}")"
  if [ "${actual_owner}" != "${expected_owner}" ]; then
    echo "Observability ${label} directory ${dir} is owned by ${actual_owner}, expected ${expected_owner}."
    echo "Refusing to change ownership of a nonempty volume automatically."
    echo "Fix with: sudo chown ${expected_owner} ${dir}"
    exit 1
  fi
}

ensure_observability_data_owner() {
  phase "ensure_observability_data_owner"
  if [ "${MINICLOUD_OPENBAO_ENABLED}" != "true" ]; then
    return
  fi

  assert_observability_dir_owner "${OBSERVABILITY_DIR}/loki" "${LOKI_DATA_UID}:${LOKI_DATA_GID}" "Loki"
  assert_observability_dir_owner "${OBSERVABILITY_DIR}/grafana" "${GRAFANA_DATA_UID}:${GRAFANA_DATA_GID}" "Grafana"
  sudo mkdir -p "${OBSERVABILITY_DIR}/telemetry"
  sudo chmod 777 "${OBSERVABILITY_DIR}/telemetry"
}

# Rendered Loki config. Must stay byte-identical (below the header) to
# observability/loki/loki.yml, which documents every setting. The deploy
# script is the versioned source on the VPS because only the script (not the
# repo) is uploaded to the server.
write_observability_loki_config() {
  cat > "${OBSERVABILITY_CONFIG_DIR}/loki.yml" <<'LOKI_CONFIG'
# Rendered by remote-deploy.sh (T10). Source of truth: observability/loki/loki.yml.
auth_enabled: true

server:
  http_listen_port: 3100
  grpc_listen_port: 9096
  log_level: info
  grpc_server_max_recv_msg_size: 8388608
  grpc_server_max_send_msg_size: 8388608

common:
  path_prefix: /loki
  storage:
    filesystem:
      chunks_directory: /loki/chunks
      rules_directory: /loki/rules
  replication_factor: 1
  ring:
    kvstore:
      store: inmemory

schema_config:
  configs:
    - from: 2025-01-01
      store: tsdb
      object_store: filesystem
      schema: v13
      index:
        prefix: index_
        period: 24h

storage_config:
  tsdb_shipper:
    active_index_directory: /loki/tsdb-index
    cache_location: /loki/tsdb-cache
  filesystem:
    directory: /loki/chunks

compactor:
  working_directory: /loki/compactor
  compaction_interval: 10m
  retention_enabled: true
  retention_delete_delay: 2h
  retention_delete_worker_count: 150
  delete_request_store: filesystem

limits_config:
  retention_period: 48h
  deletion_mode: filter-and-delete
  allow_structured_metadata: true
  reject_old_samples: true
  reject_old_samples_max_age: 48h
  max_query_length: 168h
  max_entries_limit_per_query: 1000
  split_queries_by_interval: 15m
  max_query_parallelism: 8
  max_cache_freshness_per_query: 10m

querier:
  multi_tenant_queries_enabled: false

ingester:
  lifecycler:
    ring:
      kvstore:
        store: inmemory
      replication_factor: 1
  wal:
    enabled: true
    dir: /loki/wal
  chunk_idle_period: 1h
  max_chunk_age: 2h
  chunk_encoding: snappy
  chunk_target_size: 1572864
  max_returned_stream_errors: 10

analytics:
  reporting_enabled: false
LOKI_CONFIG
}

# Rendered Grafana config. Must stay byte-identical (below the header) to
# observability/grafana/grafana.ini. Secrets come only from environment.
write_observability_grafana_config() {
  cat > "${OBSERVABILITY_CONFIG_DIR}/grafana.ini" <<'GRAFANA_CONFIG'
; Rendered by remote-deploy.sh (T10). Source of truth: observability/grafana/grafana.ini.
[server]
http_port = 3000
domain = localhost
enforce_domain = false
root_url = %(protocol)s://%(domain)s:%(http_port)s/
serve_from_sub_path = false

[paths]
data = /var/lib/grafana
logs = /var/log/grafana
plugins = /var/lib/grafana/plugins
provisioning = /etc/grafana/provisioning

[database]
path = /var/lib/grafana/grafana.db

[security]
admin_user = admin
admin_password = admin
cookie_secure = true
cookie_samesite = lax
allow_embedding = false
csrf_trusted_origins =
strict_transport_security = false

[users]
allow_sign_up = false
auto_assign_org = false
auto_assign_org_role = Viewer
default_theme = system

[auth]
disable_login_form = true
disable_signout_menu = false
oauth_auto_login = false

[auth.anonymous]
enabled = false

[auth.basic]
enabled = true

[auth.proxy]
enabled = true
header_name = X-Minicloud-Grafana-Identity
header_property = username
auto_sign_up = false
sync_ttl = 60
whitelist = 127.0.0.0/8, 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16
headers = Email:X-Minicloud-Grafana-Email, Name:X-Minicloud-Grafana-Name
enable_login_token = false

[session]
provider = file
provider_config = sessions
cookie_name = minicloud_grafana_proxy_session
cookie_secure = true
cookie_samesite = lax
session_life_time = 86400

[log]
mode = console
level = info

[metrics]
enabled = false
GRAFANA_CONFIG
}

render_observability_configs() {
  if [ "${MINICLOUD_OPENBAO_ENABLED}" != "true" ]; then
    rm -f "${OBSERVABILITY_COMPOSE_FILE}"
    return
  fi

  write_observability_loki_config
  write_observability_grafana_config

  # Operator-only Grafana server-admin credential for the private T20
  # provisioning path. Generated once per host, stored with 600 permissions
  # next to the rendered configs, then synchronized to the environment-scoped
  # OpenBao operator path during the optional observability attempt.
  local password_file="${OBSERVABILITY_CONFIG_DIR}/grafana-admin-password"
  if [ ! -f "${password_file}" ]; then
    if ! command -v openssl >/dev/null 2>&1; then
      echo "openssl is required to generate the Grafana operator password."
      exit 1
    fi
    (umask 077; openssl rand -hex 32 > "${password_file}")
  fi
  chmod 600 "${password_file}"
  local grafana_password
  grafana_password="$(cat "${password_file}")"
  if [ -z "${grafana_password}" ]; then
    echo "Grafana operator password file ${password_file} is empty."
    exit 1
  fi

  local telemetry_mgmt_key_file="${OBSERVABILITY_CONFIG_DIR}/telemetry_management_key"
  if [ ! -f "${telemetry_mgmt_key_file}" ]; then
    (umask 077; openssl rand -hex 32 > "${telemetry_mgmt_key_file}")
  fi
  chmod 600 "${telemetry_mgmt_key_file}"
  local telemetry_management_key
  telemetry_management_key="$(cat "${telemetry_mgmt_key_file}")"

  local telemetry_signing_key_file="${OBSERVABILITY_CONFIG_DIR}/telemetry_signing_key"
  if [ ! -f "${telemetry_signing_key_file}" ]; then
    (umask 077; openssl rand -hex 32 > "${telemetry_signing_key_file}")
  fi
  chmod 600 "${telemetry_signing_key_file}"
  local telemetry_auth_signing_key
  telemetry_auth_signing_key="$(cat "${telemetry_signing_key_file}")"

  # No published ports: Loki :3100 and Grafana :3000 stay container-private.
  # Telemetry :8080 binds 127.0.0.1:8088 for local host Caddy routing.
  # App services deliberately do NOT depend on loki/grafana so the API still
  # starts with telemetry disabled or unavailable.
  jq -n \
    --arg loki_image "${LOKI_IMAGE}" \
    --arg grafana_image "${GRAFANA_IMAGE}" \
    --arg telemetry_image "${TELEMETRY_IMAGE}" \
    --arg observability_dir "${OBSERVABILITY_DIR}" \
    --arg config_dir "${OBSERVABILITY_CONFIG_DIR}" \
    --arg grafana_password "${grafana_password}" \
    --arg telemetry_management_key "${telemetry_management_key}" \
    --arg telemetry_auth_signing_key "${telemetry_auth_signing_key}" \
    --arg telemetry_access_origin "$(printf '%s' "${SERVICES_JSON}" | jq -r '[.[] | .env.Telemetry__AccessOrigin // empty][0] // ""')" '
      {
        services: {
          loki: {
            image: $loki_image,
            restart: "unless-stopped",
            command: ["-config.file=/etc/loki/loki.yml"],
            volumes: [
              ($config_dir + "/loki.yml:/etc/loki/loki.yml:ro"),
              ($observability_dir + "/loki:/loki")
            ],
            healthcheck: {
              test: ["CMD-SHELL", "wget --no-verbose --tries=1 --spider http://127.0.0.1:3100/ready || exit 1"],
              interval: "10s",
              timeout: "5s",
              retries: 12,
              start_period: "20s"
            }
          },
          grafana: {
            image: $grafana_image,
            restart: "unless-stopped",
            depends_on: {loki: {condition: "service_healthy"}},
            environment: {
              GF_SECURITY_ADMIN_USER: "admin",
              GF_SECURITY_ADMIN_PASSWORD: $grafana_password
            },
            volumes: [
              ($config_dir + "/grafana.ini:/etc/grafana/grafana.ini:ro"),
              ($config_dir + "/provisioning:/etc/grafana/provisioning:ro"),
              ($observability_dir + "/grafana:/var/lib/grafana")
            ],
            healthcheck: {
              test: ["CMD-SHELL", "wget --no-verbose --tries=1 --spider http://127.0.0.1:3000/api/health || exit 1"],
              interval: "10s",
              timeout: "5s",
              retries: 12,
              start_period: "30s"
            }
          },
          telemetry: {
            image: $telemetry_image,
            restart: "unless-stopped",
            depends_on: {loki: {condition: "service_healthy"}},
            environment: {
              ASPNETCORE_URLS: "http://+:8080",
              Database__Provider: "Sqlite",
              Database__ConnectionString: "Data Source=/var/lib/minicloud/telemetry/telemetry.db",
              Telemetry__AccessOrigin: $telemetry_access_origin,
              Telemetry__LokiBaseUrl: "http://loki:3100",
              Telemetry__GrafanaBaseUrl: "http://grafana:3000",
              Telemetry__ManagementApiKey: $telemetry_management_key,
              Telemetry__AuthSigningKey: $telemetry_auth_signing_key
            },
            ports: ["127.0.0.1:8088:8080"],
            volumes: [
              ($observability_dir + "/telemetry:/var/lib/minicloud/telemetry")
            ],
            healthcheck: {
              test: ["CMD-SHELL", "wget --no-verbose --tries=1 --output-document=/dev/null http://127.0.0.1:8080/health || exit 1"],
              interval: "10s",
              timeout: "5s",
              retries: 6,
              start_period: "15s"
            }
          }
        }
      }
    ' > "${OBSERVABILITY_COMPOSE_FILE}"
  chmod 600 "${OBSERVABILITY_COMPOSE_FILE}"
}

write_observability_caddy_config() {
  local caddy_obs_file="/opt/minicloud/caddy/apps/${APP_NAME}-observability.caddy"
  local telemetry_domain logs_domain
  telemetry_domain="$(printf '%s' "${SERVICES_JSON}" | jq -r '[.[] | .env.Telemetry__IngestionUrl // empty][0] // ""' | sed -E 's#^https://([^/]+)/.*$#\1#')"
  logs_domain="$(printf '%s' "${SERVICES_JSON}" | jq -r '[.[] | .env.Telemetry__AccessOrigin // empty][0] // ""' | sed -E 's#^https://([^/]+)/?$#\1#')"
  for hostname in "${telemetry_domain}" "${logs_domain}"; do
    if ! [[ "${hostname}" =~ ^[a-zA-Z0-9]([a-zA-Z0-9.-]*[a-zA-Z0-9])?$ ]]; then
      echo "Configure HTTPS Telemetry__IngestionUrl and Telemetry__AccessOrigin before enabling observability." >&2
      return 1
    fi
  done

  sudo mkdir -p /opt/minicloud/caddy/apps
  sudo tee "${caddy_obs_file}" >/dev/null <<CADDY_CONFIG
${telemetry_domain} {
  reverse_proxy 127.0.0.1:8088
}

${logs_domain} {
  reverse_proxy 127.0.0.1:8088
}
CADDY_CONFIG
  sudo chmod 0644 "${caddy_obs_file}"
  sudo caddy validate --config /etc/caddy/Caddyfile || true
  reload_caddy || true
}

wait_for_observability_service() {
  local service="$1"
  local url="$2"
  local label="$3"

  for attempt in $(seq 1 30); do
    if observability_compose exec -T "${service}" wget --no-verbose --tries=1 --timeout=5 --spider "${url}" </dev/null >/dev/null 2>&1 \
       || observability_compose exec -T "${service}" curl -fsS --max-time 5 "${url}" </dev/null >/dev/null 2>&1; then
      echo "${label} is ready."
      return 0
    fi
    sleep 2
  done

  return 1
}

start_observability() {
  phase "start_observability"
  if [ "${MINICLOUD_OPENBAO_ENABLED}" != "true" ]; then
    return
  fi

  observability_compose pull
  observability_compose up -d loki grafana telemetry

  # Independent health checks for the log stack. A degraded log stack must
  # never fail the completed app rollout, so failure here only warns:
  # the API is already running. Storage and configuration checks apply only
  # to this optional subprocess.
  local observability_ready="true"
  if ! wait_for_observability_service loki "http://127.0.0.1:3100/ready" "Loki"; then
    echo "WARNING: Loki did not become ready; app services remain deployed without application-log storage."
    observability_ready="false"
  fi
  if ! wait_for_observability_service grafana "http://127.0.0.1:3000/api/health" "Grafana"; then
    echo "WARNING: Grafana did not become ready; app services remain deployed without the log viewer."
    observability_ready="false"
  fi
  if ! wait_for_observability_service telemetry "http://127.0.0.1:8080/health" "Telemetry"; then
    echo "WARNING: Telemetry did not become ready; app services remain deployed without dedicated telemetry."
    observability_ready="false"
  fi

  if [ "${observability_ready}" = "true" ]; then
    write_observability_caddy_config
  fi

  if [ "${observability_ready}" != "true" ]; then
    observability_compose ps || true
  fi
}

write_openbao_config() {
  cat > "${OPENBAO_DIR}/config/openbao.hcl" <<'OPENBAO_CONFIG'
disable_mlock = true
ui = false

storage "raft" {
  path = "/openbao/data"
  node_id = "minicloud-openbao"
}

listener "tcp" {
  address = "0.0.0.0:8200"
  tls_disable = true
}

api_addr = "http://openbao:8200"
cluster_addr = "http://openbao:8201"
OPENBAO_CONFIG
}

wait_for_openbao() {
  local ready="false"
  for attempt in $(seq 1 30); do
    if compose exec -T openbao \
      sh -c 'BAO_ADDR=http://127.0.0.1:8200 bao status -format=json >/tmp/openbao-status.json 2>/dev/null || test "$?" = "2"' \
      </dev/null; then
      ready="true"
      break
    fi
    sleep 2
  done

  if [ "${ready}" != "true" ]; then
    echo "OpenBao did not start."
    compose_diagnostics
    exit 1
  fi
}

configure_openbao_api_token() {
  local token_metadata
  token_metadata="$(
    compose exec -T \
    -e BAO_ADDR=http://127.0.0.1:8200 \
    -e BAO_TOKEN="${OPENBAO_TOKEN}" \
    -e OPENBAO_MOUNT_PATH="${OPENBAO_MOUNT_PATH}" \
    -e OPENBAO_ENVIRONMENT_PREFIX="${OPENBAO_ENVIRONMENT_PREFIX}" \
    -e OPENBAO_API_TOKEN="${MINICLOUD_OPENBAO_API_TOKEN}" \
    openbao sh -c '
set -eu
cat > /tmp/minicloud-api-policy.hcl <<EOF
path "auth/token/renew-self" {
  capabilities = ["update"]
}

path "${OPENBAO_MOUNT_PATH}/data/env/${OPENBAO_ENVIRONMENT_PREFIX}/orgs/*" {
  capabilities = ["create", "update", "read", "delete", "list"]
}

path "${OPENBAO_MOUNT_PATH}/data/env/${OPENBAO_ENVIRONMENT_PREFIX}/ops/grafana/server-admin" {
  capabilities = ["read"]
}

path "${OPENBAO_MOUNT_PATH}/metadata/env/${OPENBAO_ENVIRONMENT_PREFIX}/orgs/*" {
  capabilities = ["create", "update", "read", "delete", "list"]
}
EOF
bao policy write minicloud-api /tmp/minicloud-api-policy.hcl >/dev/null
if ! bao token lookup "$OPENBAO_API_TOKEN" >/dev/null 2>&1; then
  bao token create -id="$OPENBAO_API_TOKEN" -policy=minicloud-api -orphan -period=168h >/dev/null
fi
bao token lookup -format=json "$OPENBAO_API_TOKEN"
' \
    </dev/null
  )"
  # Parse on the host: token lookup supports JSON output, not -field.
  if ! printf '%s' "${token_metadata}" | jq -e '.data | .period == 604800 and .explicit_max_ttl == 0 and .orphan == true' >/dev/null 2>&1; then
    echo "OpenBao API token must be orphaned and periodic (168h) without an explicit maximum TTL. Set MINICLOUD_OPENBAO_API_TOKEN to a new unique value and redeploy."
    return 1
  fi
  unset token_metadata

  compose exec -T \
    -e BAO_ADDR=http://127.0.0.1:8200 \
    -e BAO_TOKEN="${OPENBAO_TOKEN}" \
    -e OPENBAO_MOUNT_PATH="${OPENBAO_MOUNT_PATH}" \
    -e OPENBAO_ENVIRONMENT_PREFIX="${OPENBAO_ENVIRONMENT_PREFIX}" \
    -e OPENBAO_API_TOKEN="${MINICLOUD_OPENBAO_API_TOKEN}" \
    openbao sh -c '
set -eu
BAO_TOKEN="$OPENBAO_API_TOKEN" bao token renew >/dev/null
BAO_TOKEN="$OPENBAO_API_TOKEN" bao kv put "$OPENBAO_MOUNT_PATH/env/$OPENBAO_ENVIRONMENT_PREFIX/orgs/__deploy_smoke__/apps/__deploy_smoke__/services/backend/__deploy_smoke__" value=ok >/dev/null
BAO_TOKEN="$OPENBAO_API_TOKEN" bao kv get -field=value "$OPENBAO_MOUNT_PATH/env/$OPENBAO_ENVIRONMENT_PREFIX/orgs/__deploy_smoke__/apps/__deploy_smoke__/services/backend/__deploy_smoke__" >/dev/null
BAO_TOKEN="$OPENBAO_API_TOKEN" bao kv metadata delete "$OPENBAO_MOUNT_PATH/env/$OPENBAO_ENVIRONMENT_PREFIX/orgs/__deploy_smoke__/apps/__deploy_smoke__/services/backend/__deploy_smoke__" >/dev/null
' \
    </dev/null
}

configure_openbao() {
  phase "configure_openbao"
  if [ "${MINICLOUD_OPENBAO_ENABLED}" != "true" ]; then
    return
  fi

  write_openbao_config
  compose up -d openbao
  wait_for_openbao

  local initialized
  initialized="$(
    compose exec -T openbao \
      sh -c 'BAO_ADDR=http://127.0.0.1:8200 bao status -format=json 2>/dev/null || true' \
      </dev/null \
    | jq -r '.initialized // false'
  )"

  if [ "${initialized}" != "true" ]; then
    if [ -n "${MINICLOUD_OPENBAO_UNSEAL_KEY}" ] || [ -n "${MINICLOUD_OPENBAO_ROOT_TOKEN}" ]; then
      echo "OpenBao is uninitialized; provided GitHub root/unseal secrets do not apply to this new store and will be replaced by generated values."
    fi
    compose exec -T openbao \
      sh -c 'BAO_ADDR=http://127.0.0.1:8200 bao operator init -key-shares=1 -key-threshold=1 -format=json' \
      </dev/null \
      > "${OPENBAO_BOOTSTRAP_FILE}"
    chmod 600 "${OPENBAO_BOOTSTRAP_FILE}"
    echo "OpenBao initialized. Copy the generated unseal key and root token from ${OPENBAO_BOOTSTRAP_FILE} into GitHub secrets."
    OPENBAO_UNSEAL_KEY="$(jq -r '.unseal_keys_b64[0]' "${OPENBAO_BOOTSTRAP_FILE}")"
    OPENBAO_TOKEN="$(jq -r '.root_token' "${OPENBAO_BOOTSTRAP_FILE}")"
    echo "__MINICLOUD_OPENBAO_UNSEAL_KEY__=${OPENBAO_UNSEAL_KEY}"
    echo "__MINICLOUD_OPENBAO_ROOT_TOKEN__=${OPENBAO_TOKEN}"
  else
    if [ -z "${MINICLOUD_OPENBAO_UNSEAL_KEY}" ] || [ -z "${MINICLOUD_OPENBAO_ROOT_TOKEN}" ]; then
      echo "MINICLOUD_OPENBAO_UNSEAL_KEY and MINICLOUD_OPENBAO_ROOT_TOKEN secrets are required after OpenBao has been initialized."
      exit 1
    fi
    OPENBAO_UNSEAL_KEY="${MINICLOUD_OPENBAO_UNSEAL_KEY}"
    OPENBAO_TOKEN="${MINICLOUD_OPENBAO_ROOT_TOKEN}"
  fi

  if [ -z "${OPENBAO_UNSEAL_KEY}" ] || [ "${OPENBAO_UNSEAL_KEY}" = "null" ] || [ -z "${OPENBAO_TOKEN}" ] || [ "${OPENBAO_TOKEN}" = "null" ]; then
    echo "OpenBao bootstrap material is missing required unseal key or root token."
    exit 1
  fi

  compose exec -T \
    -e BAO_ADDR=http://127.0.0.1:8200 \
    -e BAO_UNSEAL_KEY="${OPENBAO_UNSEAL_KEY}" \
    openbao sh -c 'bao operator unseal "$BAO_UNSEAL_KEY" >/dev/null || true' \
    </dev/null

  compose exec -T \
    -e BAO_ADDR=http://127.0.0.1:8200 \
    -e BAO_TOKEN="${OPENBAO_TOKEN}" \
    -e OPENBAO_MOUNT_PATH="${OPENBAO_MOUNT_PATH}" \
    openbao sh -c 'bao secrets enable -path="$OPENBAO_MOUNT_PATH" kv-v2 >/dev/null 2>&1 || true' \
    </dev/null

  configure_openbao_api_token
}

start_infra() {
  phase "start_infra"
  if has_external_ghcr_images; then
    echo "${GHCR_TOKEN}" | docker login ghcr.io -u "${GHCR_USERNAME}" --password-stdin
  fi

  if { [ -z "${BUILD_ARTIFACTS_JSON:-}" ] || [ "${BUILD_ARTIFACTS_JSON}" = "null" ]; } && [ "$(jq '.managed_images // [] | length' "${PAYLOAD_FILE}")" -eq 0 ]; then
    compose pull
  else
    if [ -f "${INFRA_COMPOSE_FILE}" ]; then
      docker compose -p "minicloud-${APP_NAME}" -f "${INFRA_COMPOSE_FILE}" pull
    fi

    local external_services_str=""
    if [ -n "${SERVICES_JSON:-}" ] && [ "${SERVICES_JSON}" != "null" ]; then
      external_services_str="$(printf '%s' "${SERVICES_JSON}" | jq -r '[.[] | select(.managed != true and (.image // "") != "" and (.image | startswith("minicloud/") | not)) | .name] | join(" ")')"
    fi
    if [ -n "${external_services_str}" ]; then
      # shellcheck disable=SC2086
      compose pull ${external_services_str}
    fi
  fi

  configure_openbao

  if [ "${DATABASE}" = "postgres" ]; then
    compose up -d db
  fi
}

sync_postgres_password() {
  phase "sync_postgres_password"
  if [ "${DATABASE}" != "postgres" ] || [ "${POSTGRES_PASSWORD_UPDATE}" != "true" ]; then
    return
  fi

  local ready="false"
  for attempt in $(seq 1 30); do
    if compose exec -T db pg_isready -U minicloud -d "${POSTGRES_DB}" </dev/null >/dev/null; then
      ready="true"
      break
    fi
    sleep 2
  done

  if [ "${ready}" != "true" ]; then
    echo "Postgres did not become ready before password sync."
    compose_diagnostics
    exit 1
  fi

  PASSWORD_SQL_LITERAL="$(printf '%s' "${POSTGRES_PASSWORD}" | sed "s/'/''/g")"
  SQL="ALTER USER minicloud WITH PASSWORD '${PASSWORD_SQL_LITERAL}';"
  compose exec -T db psql -U minicloud -d "${POSTGRES_DB}" --set=ON_ERROR_STOP=1 -c "${SQL}" </dev/null
}

start_app_services() {
  phase "start_app_services"
  local -a app_service_names
  mapfile -t app_service_names < <(printf '%s' "${SERVICES_JSON}" | jq -r '.[].name')
  # Preserve optional containers in the same project. Service removal must
  # be explicit; absence from this config must not delete running containers.
  if ! compose up -d --wait --wait-timeout 300 "${app_service_names[@]}"; then
    echo "Compose services did not become ready."
    compose_diagnostics
    exit 1
  fi
}

verify_local_health() {
  phase "verify_local_health"
  local health_dir
  health_dir="$(mktemp -d "${APP_DIR}/shared/health.XXXXXX")"
  local -a services=()
  while IFS= read -r service; do
    services+=("${service}")
  done < <(printf '%s' "${SERVICES_JSON}" | jq -c '[.[] | select(.public // false)] | .[]')
  local service_count="${#services[@]}"
  local health_limit=$((MAX_PARALLEL_NETWORK_OPERATIONS < service_count ? MAX_PARALLEL_NETWORK_OPERATIONS : service_count))
  echo "TIMING phase=local_health deployment_id=${DEPLOYMENT_ID} service_count=${service_count} configured_concurrency=${MAX_PARALLEL_NETWORK_OPERATIONS} effective_concurrency=${health_limit}"
  local -a health_pids=()
  local running=0

  probe_local_service() {
    local service="$1"
    local result_file="$2"
    local service_name service_host_port service_health_path health_url last_output curl_pid
    service_name="$(printf '%s' "${service}" | jq -r '.name')"
    service_host_port="$(printf '%s' "${service}" | jq -r '.host_port')"
    service_health_path="$(printf '%s' "${service}" | jq -r '.health_path // "/health"')"
    health_url="http://127.0.0.1:${service_host_port}${service_health_path}"
    last_output=""
    curl_pid=""

    stop_probe() {
      if [ -n "${curl_pid}" ]; then
        kill "${curl_pid}" 2>/dev/null || true
        wait "${curl_pid}" 2>/dev/null || true
      fi
      exit 143
    }
    trap stop_probe TERM INT

    for attempt in $(seq 1 30); do
      local output_file="${result_file}.output"
      curl -fsS --max-time 5 "${health_url}" >"${output_file}" 2>&1 &
      curl_pid="$!"
      if wait "${curl_pid}"; then
        curl_pid=""
        rm -f "${output_file}"
        return 0
      fi
      curl_pid=""
      last_output="$(cat "${output_file}")"
      rm -f "${output_file}"
      if [ "${attempt}" -lt 30 ]; then
        sleep 2
      fi
    done

    printf 'Health check failed for %s at %s\n%s\n' "${service_name}" "${health_url}" "${last_output}" > "${result_file}"
    return 1
  }

  stop_health_checks() {
    local pid
    for pid in "${health_pids[@]:-}"; do
      kill "${pid}" 2>/dev/null || true
    done
    for pid in "${health_pids[@]:-}"; do
      wait "${pid}" 2>/dev/null || true
    done
  }

  cancel_health_checks() {
    trap - TERM INT
    stop_health_checks
    rm -rf "${health_dir}"
    exit 143
  }
  trap cancel_health_checks TERM INT

  reap_one_health_check() {
    local slot pid
    while true; do
      for slot in "${!health_pids[@]}"; do
        pid="${health_pids[${slot}]}"
        if ! kill -0 "${pid}" 2>/dev/null; then
          if ! wait "${pid}"; then
            return 1
          fi
          unset 'health_pids[slot]'
          return 0
        fi
      done
      sleep 0.1
    done
  }

  local index
  for index in "${!services[@]}"; do
    probe_local_service "${services[${index}]}" "${health_dir}/${index}.failure" &
    health_pids+=("$!")
    running=$((running + 1))
    if [ "${running}" -ge "${health_limit}" ]; then
      if ! reap_one_health_check; then
        stop_health_checks
        trap - TERM INT
        cat "${health_dir}"/*.failure 2>/dev/null || true
        rm -rf "${health_dir}"
        compose_diagnostics
        return 1
      fi
      running=$((running - 1))
    fi
  done

  while [ "${running}" -gt 0 ]; do
    if ! reap_one_health_check; then
      stop_health_checks
      trap - TERM INT
      cat "${health_dir}"/*.failure 2>/dev/null || true
      rm -rf "${health_dir}"
      compose_diagnostics
      return 1
    fi
    running=$((running - 1))
  done
  trap - TERM INT
  rm -rf "${health_dir}"
}

activate_caddy() {
  phase "activate_caddy"

  if [ -f "${CADDY_APP}" ]; then
    sudo cp "${CADDY_APP}" "${CADDY_PREVIOUS}"
  fi

  if ! sudo install -m 0644 "${CADDY_NEXT}" "${CADDY_APP}" || ! sudo caddy validate --config /etc/caddy/Caddyfile || ! reload_caddy; then
    echo "Caddy activation failed; restoring previous config."
    if [ -f "${CADDY_PREVIOUS}" ]; then
      sudo install -m 0644 "${CADDY_PREVIOUS}" "${CADDY_APP}"
      sudo caddy validate --config /etc/caddy/Caddyfile || true
      reload_caddy || true
    fi
    exit 1
  fi
}

cleanup_deployment_artifacts() {
  phase "cleanup_deployment_artifacts"
  # Do not prune volumes or networks. Docker retains container-referenced images.
  # Only this app's obsolete release aliases are removed below. Shared base and
  # dependency images remain eligible for reuse. No global image/container prune.
  if [ -n "$BUILD_BUILDER" ]; then maintain_build_cache || return 1; fi
  local image_refs image
  image_refs="$(docker image ls --format '{{.Repository}}:{{.Tag}}')" || return 1
  while IFS= read -r image; do
    case "$image" in
      "minicloud/${APP_NAME}-"*:*)
        if [ "$image" = "${IMAGE_REF:-}" ]; then continue; fi
        if ! printf '%s' "$SERVICES_JSON" | jq -e --arg image "$image" 'any(.[]; .image == $image)' >/dev/null; then
          # Remove obsolete aliases even when they share a current image ID.
          docker image rm "$image" || return 1
        fi
        ;;
    esac
  done <<< "$image_refs"
  local managed_scope
  managed_scope=""
  if [ -f "${PAYLOAD_FILE:-}" ]; then managed_scope="$(jq -r '.managed_image_scope // ""' "${PAYLOAD_FILE}")"; fi
  if [[ "${managed_scope}" =~ ^[a-z0-9.:-]+/org/[a-z0-9_-]+/app/[a-z0-9_-]+/service/$ ]]; then
    image_refs="$(docker image ls --digests --format '{{.Repository}}@{{.Digest}}')" || return 1
    while IFS= read -r image; do
      if [[ "${image}" == "${managed_scope}"*@sha256:* ]] && ! printf '%s' "$SERVICES_JSON" | jq -e --arg image "$image" 'any(.[]; .image == $image)' >/dev/null; then
        docker image rm "$image" || return 1
      fi
    done <<< "$image_refs"
  fi
}

record_deploy() {
  phase "record_deploy"
  if [ ! -f "${DEPLOYS_FILE}" ]; then
    echo "[]" > "${DEPLOYS_FILE}"
  fi

  local recorded_image="${IMAGE_REF}"
  if [ -z "${recorded_image}" ] && [ -n "${SERVICES_JSON:-}" ] && [ "${SERVICES_JSON}" != "null" ]; then
    recorded_image="$(printf '%s' "${SERVICES_JSON}" | jq -r '.[0].image // empty')"
  fi
  local recorded_tag="${IMAGE_TAG:-${DEPLOYMENT_ID}}"

  TMP_FILE="$(mktemp)"
  jq \
    --arg image "${recorded_image}" \
    --arg imageTag "${recorded_tag}" \
    --arg commitSha "${GITHUB_SHA}" \
    --arg deployedAt "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
    --arg status "healthy" \
    --arg deploymentType "${DEPLOYMENT_TYPE}" \
    --argjson services "${SERVICES_JSON}" \
    '. + [{
      image: $image,
      imageTag: $imageTag,
      commitSha: $commitSha,
      deployedAt: $deployedAt,
      status: $status,
      database: "'"${DATABASE}"'",
      deploymentType: $deploymentType,
      services: $services
    }]' "${DEPLOYS_FILE}" > "${TMP_FILE}"
  mv "${TMP_FILE}" "${DEPLOYS_FILE}"
}

observability_compose() {
  docker compose -p "minicloud-${APP_NAME}" -f "${OBSERVABILITY_COMPOSE_FILE}" "$@"
}

sync_grafana_operator_secret() {
  local operator_token="${MINICLOUD_OPENBAO_ROOT_TOKEN}"
  if [ -z "${operator_token}" ] && [ -f "${OPENBAO_BOOTSTRAP_FILE}" ]; then
    operator_token="$(jq -r '.root_token // empty' "${OPENBAO_BOOTSTRAP_FILE}")"
  fi
  if [ -z "${operator_token}" ]; then
    echo "OpenBao operator credential is unavailable; skipping Grafana provisioning setup."
    return 1
  fi
  compose exec -T \
    -e BAO_ADDR=http://127.0.0.1:8200 \
    -e BAO_TOKEN="${operator_token}" \
    -e OPENBAO_MOUNT_PATH="${OPENBAO_MOUNT_PATH}" \
    -e OPENBAO_ENVIRONMENT_PREFIX="${OPENBAO_ENVIRONMENT_PREFIX}" \
    -e OPENBAO_API_TOKEN="${MINICLOUD_OPENBAO_API_TOKEN}" \
    openbao sh -c '
set -eu
secret_path="$OPENBAO_MOUNT_PATH/env/$OPENBAO_ENVIRONMENT_PREFIX/ops/grafana/server-admin"
bao kv put "$secret_path" value=- >/dev/null
BAO_TOKEN="$OPENBAO_API_TOKEN" bao kv get -field=value "$secret_path" >/dev/null
' < "${OBSERVABILITY_CONFIG_DIR}/grafana-admin-password"
}

observability_main() {
  if [ "${MINICLOUD_OPENBAO_ENABLED}" != "true" ]; then
    return
  fi
  if ! mountpoint -q "${OBSERVABILITY_DIR}"; then
    echo "Observability storage requires mounted volume at ${OBSERVABILITY_DIR}; skipping log stack."
    exit 1
  fi
  if [ ! -d "${OBSERVABILITY_DIR}/loki" ] || [ ! -d "${OBSERVABILITY_DIR}/grafana" ]; then
    echo "Observability volume must contain loki/ and grafana/ directories; skipping log stack."
    exit 1
  fi
  ensure_observability_data_owner
  mkdir -p "${OBSERVABILITY_CONFIG_DIR}/provisioning"
  render_observability_configs
  observability_compose config >/dev/null
  sync_grafana_operator_secret
  start_observability
}

deploy_observability_optional() {
  if [ "${MINICLOUD_OPENBAO_ENABLED}" != "true" ]; then
    return
  fi
  # A separate shell preserves errexit inside optional work. Calling a shell
  # function directly in an `if` would suppress errexit throughout that function.
  if ! timeout --kill-after=10s 180s bash "${BASH_SOURCE[0]}" "${PAYLOAD_FILE}" --observability; then
    echo "WARNING: Observability setup failed or timed out; app services remain deployed."
  fi
}

verify_image_asset_requirements() {
  local service image requirements
  while IFS= read -r service; do
    image="$(printf '%s' "$service" | jq -r '.image')"
    # Locally built/retained consumer images exist before container replacement.
    if ! requirements="$(docker image inspect --format '{{json .Config.Labels}}' "$image" 2>/dev/null)"; then continue; fi
    requirements="$(printf '%s' "$requirements" | jq -c '."dev.minicloud.asset-mounts" // "{}" | fromjson')"
    if ! jq -en --argjson requirements "$requirements" --argjson service "$service" '
      ($requirements | type == "object" and length <= 10) and
      all($requirements | to_entries[]; . as $required |
        any($service.asset_mounts[]?; .name == $required.key and .mount_path == $required.value and .protocol_version == 1))
    ' >/dev/null; then
      echo "ERROR: asset_release_invalid: consumer image requires an explicit compatible asset pin before activation." >&2
      return 1
    fi
  done < <(printf '%s' "$SERVICES_JSON" | jq -c '.[]')
}

verify_runtime_agent_plan() {
  local verification
  verification="$(jq -r --arg key "${1:-verification}" '.runtime_agent[$key] // empty' "${PAYLOAD_FILE}")"
  [ -n "${verification}" ] || return 12
  sudo python3 - "${verification}" <<'PY_AGENT_VERIFY'
import base64, hashlib, hmac, json, os, pathlib, pwd, stat, subprocess, sys
expected = json.loads(base64.b64decode(sys.argv[1]))
root = pathlib.Path('/opt/minicloud/agent')
try:
    agent = pwd.getpwnam('minicloud-agent')
    if root.is_symlink() or root.stat().st_uid != 0 or root.stat().st_mode & 0o022:
        sys.exit(12)
    for name, digest in expected['files'].items():
        path = root / name
        mode = path.lstat()
        if not stat.S_ISREG(mode.st_mode) or mode.st_uid != 0 or mode.st_mode & 0o022:
            sys.exit(12)
        if hashlib.sha256(path.read_bytes()).hexdigest() != digest:
            sys.exit(12)
    unit = pathlib.Path('/etc/systemd/system/minicloud-agent.service')
    if unit.is_symlink() or unit.read_bytes() != (root / 'minicloud-agent.service').read_bytes():
        sys.exit(12)
    running = subprocess.run(['systemctl', 'is-active', '--quiet', 'minicloud-agent.service']).returncode == 0
    for field, wanted in [('FragmentPath', '/etc/systemd/system/minicloud-agent.service'), ('User', 'minicloud-agent'), ('DropInPaths', '')]:
        if subprocess.check_output(['systemctl', 'show', '-p', field, '--value', 'minicloud-agent.service'], text=True).strip() != wanted:
            sys.exit(12)
    sudoers = pathlib.Path('/etc/sudoers.d/minicloud-agent-routing')
    if sudoers.is_symlink() or sudoers.stat().st_uid != 0 or stat.S_IMODE(sudoers.stat().st_mode) != 0o440 or sudoers.read_text().strip() != 'minicloud-agent ALL=(root) NOPASSWD: /opt/minicloud/agent/apply-minicloud-routing ""':
        sys.exit(12)
    if subprocess.check_output(['systemctl', 'show', '-p', 'NeedDaemonReload', '--value', 'minicloud-agent.service'], text=True).strip() != 'no':
        sys.exit(12)
    pid = subprocess.check_output(['systemctl', 'show', '-p', 'MainPID', '--value', 'minicloud-agent.service'], text=True).strip()
    if running and (not pid.isdigit() or pathlib.Path('/proc/' + pid + '/exe').resolve() != root / 'minicloud-agent'):
        sys.exit(12)
    configuration = root / 'config.json'
    token = root / 'runtime-token'
    if configuration.is_symlink() or token.is_symlink() or configuration.stat().st_mode & 0o027 or token.stat().st_mode & 0o077 or configuration.stat().st_uid != 0 or token.stat().st_uid != agent.pw_uid or configuration.stat().st_gid != agent.pw_gid:
        sys.exit(12)
    credential = expected.get('credentialHash')
    if not credential:
        sys.exit(11)
    kind, rounds, salt, wanted = credential.split('$')
    if kind != 'pbkdf2_sha256' or not 1 <= int(rounds) <= 1000000:
        sys.exit(11)
    actual = hashlib.pbkdf2_hmac('sha256', token.read_text().strip().encode(), base64.b64decode(salt), int(rounds))
    if not hmac.compare_digest(actual, base64.b64decode(wanted)):
        sys.exit(11)
    if json.loads(configuration.read_text()) != expected['config']:
        sys.exit(10)
    if not running:
        sys.exit(13)
except (OSError, ValueError, KeyError):
    sys.exit(12)
sys.exit(0)
PY_AGENT_VERIFY
}

install_runtime_agent() {
  if ! jq -e '.runtime_agent != null' "${PAYLOAD_FILE}" >/dev/null; then return; fi
  phase "runtime_agent_install"
  local bundle_dir action observed
  bundle_dir="$(dirname "${PAYLOAD_FILE}")"
  action="$(jq -r '.runtime_agent.action // "install"' "${PAYLOAD_FILE}")"
  worker_lease_valid || { echo "ERROR: worker_lease_expired: refusing agent installation." >&2; return 1; }
  if [ "${action}" != "install" ]; then
    observed=0
    verify_runtime_agent_plan || observed=$?
    case "${action}:${observed}" in
      reuse:0) echo "Runtime agent: action=reuse reason=verified_unchanged uploads=0 restarts=0 credentials=0"; return ;;
      restart:13)
        worker_lease_valid || return 1
        sudo systemctl restart minicloud-agent.service
        sudo systemctl is-active --quiet minicloud-agent.service
        verify_runtime_agent_plan final_verification
        echo "Runtime agent: action=restart reason=verified_files_service_stopped uploads=0 restarts=1 credentials=0"
        return ;;
      configure:10|rotate:0|rotate:10|rotate:11|rotate:13) ;;
      *) echo "ERROR: runtime_agent_install_failed: host changed after agent observation; retry deployment." >&2; return 1 ;;
    esac
  fi
  case "${action}" in
    install)
      sudo install -d -o root -g root -m 0755 /opt/minicloud/agent
      sudo systemctl stop minicloud-agent.service 2>/dev/null || true
      sudo install -o root -g root -m 0755 "${bundle_dir}/minicloud-agent" /opt/minicloud/agent/minicloud-agent
      sudo install -o root -g root -m 0755 "${bundle_dir}/apply-minicloud-routing.py" /opt/minicloud/agent/apply-minicloud-routing
      sudo install -o root -g root -m 0644 "${bundle_dir}/minicloud-agent.service" /opt/minicloud/agent/minicloud-agent.service
      sudo install -o root -g root -m 0700 "${bundle_dir}/install-minicloud-agent.sh" /opt/minicloud/agent/install-minicloud-agent.sh
      ;;
    configure|rotate) sudo systemctl stop minicloud-agent.service ;;
    *) echo "ERROR: runtime_agent_install_failed: invalid agent action." >&2; return 1 ;;
  esac
  # Stage root-owned files beside their destination and rename atomically.
  sudo install -o root -g root -m 0600 /dev/null /opt/minicloud/agent/config.json.next
  jq '.runtime_agent.config' "${PAYLOAD_FILE}" | sudo tee /opt/minicloud/agent/config.json.next >/dev/null
  if [ "${action}" != "configure" ]; then
    sudo install -o root -g root -m 0600 /dev/null /opt/minicloud/agent/runtime-token.next
    jq -r '.runtime_agent.token' "${PAYLOAD_FILE}" | sudo tee /opt/minicloud/agent/runtime-token.next >/dev/null
    if [ "${action}" = "rotate" ]; then sudo chown minicloud-agent:minicloud-agent /opt/minicloud/agent/runtime-token.next; fi
    sudo mv -f /opt/minicloud/agent/runtime-token.next /opt/minicloud/agent/runtime-token
  fi
  if [ "${action}" != "install" ]; then
    sudo chown root:minicloud-agent /opt/minicloud/agent/config.json.next
    sudo chmod 0640 /opt/minicloud/agent/config.json.next
  fi
  sudo mv -f /opt/minicloud/agent/config.json.next /opt/minicloud/agent/config.json
  worker_lease_valid || { echo "ERROR: worker_lease_expired: refusing agent restart." >&2; return 1; }
  if [ "${action}" = "install" ]; then
    sudo bash /opt/minicloud/agent/install-minicloud-agent.sh
  else
    sudo systemctl restart minicloud-agent.service
  fi
  sudo systemctl is-active --quiet minicloud-agent.service
  verify_runtime_agent_plan final_verification
  worker_lease_valid || { echo "ERROR: worker_lease_expired: refusing agent installation." >&2; return 1; }
  echo "Runtime agent: action=${action} restarts=1"
}

main() {
  preflight
  start_worker_lease_watchdog
  trap 'stop_worker_lease_watchdog' EXIT
  acquire_lock
  install_runtime_maintenance
  if jq -e '.asset_publication != null' "${PAYLOAD_FILE}" >/dev/null; then
    mkdir -p "${APP_DIR}"
    prepare_builder
    build_artifacts
    phase "asset_publication"
    worker_lease_valid || { echo "ERROR: worker_lease_expired: refusing asset publication." >&2; exit 1; }
    python3 "$(dirname "${PAYLOAD_FILE}")/asset-release.py" publish "${APP_DIR}" "${PAYLOAD_FILE}"
    worker_lease_valid || { echo "ERROR: worker_lease_expired: asset publication ownership lost." >&2; exit 1; }
    docker image rm "$(jq -r '.asset_publication.image' "${PAYLOAD_FILE}")" >/dev/null
    rm -rf "${APP_DIR}/builds/${DEPLOYMENT_ID}"
    maintain_build_cache
    phase "complete"
    finish_phase succeeded
    CURRENT_PHASE_STARTED_MS=""
    echo "__MINICLOUD_DEPLOY_COMPLETE__=${GITHUB_SHA}"
    return
  fi
  prepare_paths
  prepare_minicloud_registry
  verify_retained_images
  if [ "$(printf '%s' "${BUILD_ARTIFACTS_JSON:-[]}" | jq 'length')" -gt 0 ]; then prepare_builder; fi
  build_artifacts
  prepare_managed_images
  wait_for_rollout_gate
  verify_image_asset_requirements
  if jq -e '[.services[].asset_mounts[]?] | length > 0' "${PAYLOAD_FILE}" >/dev/null; then
    python3 "$(dirname "${PAYLOAD_FILE}")/asset-release.py" verify "${APP_DIR}" "${PAYLOAD_FILE}"
  fi
  if jq -e '[.services[].storage_mounts[]?] | length > 0' "${PAYLOAD_FILE}" >/dev/null; then
    phase "storage_mounts"
    sudo python3 "$(dirname "${PAYLOAD_FILE}")/storage-volume.py" "${PAYLOAD_FILE}"
  fi
  render_next_configs
  validate_next_configs
  migrate_postgres_if_needed
  ensure_postgres_data_owner
  start_infra
  sync_postgres_password
  start_app_services
  verify_local_health
  activate_caddy_with_lease_fence
  install_runtime_agent
  record_deploy
  deploy_observability_optional
  if ! cleanup_deployment_artifacts; then
    echo "WARNING: Post-deployment artifact cleanup failed; current services remain deployed."
  fi
  phase "complete"
  finish_phase succeeded
  CURRENT_PHASE_STARTED_MS=""
  stop_worker_lease_watchdog
  LEASE_WATCHDOG_PID=""
  case "$(uname -m)" in
    x86_64|amd64) echo '__MINICLOUD_BUILD_PLATFORM__=linux/amd64' ;;
    aarch64|arm64) echo '__MINICLOUD_BUILD_PLATFORM__=linux/arm64' ;;
  esac
  echo "__MINICLOUD_DEPLOY_COMPLETE__=${GITHUB_SHA}"
  echo "Deployed ${IMAGE_REF:-${DEPLOYMENT_ID}}"
}

if [ "${2:-}" = "--observability" ]; then
  trap - ERR
  observability_main
else
  main
fi
