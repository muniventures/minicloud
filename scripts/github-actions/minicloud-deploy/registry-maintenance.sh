#!/usr/bin/env bash
set -Eeuo pipefail
umask 077

APP_DIR="${1:-/opt/minicloud/apps/minicloud}"
MODE="${2:-gc}"
[[ "${MODE}" == gc || "${MODE}" == backup ]] || { echo 'Expected gc or backup.' >&2; exit 2; }
APP_CONFIG="${APP_DIR}/app.compose.json"
INFRA_CONFIG="${APP_DIR}/infra.compose.json"
ROOT="$(jq -r '.services.backend.environment.DeploymentArtifacts__RootPath' "${APP_CONFIG}")"
mountpoint -q "${ROOT}" || { echo 'Registry storage mount is unavailable.' >&2; exit 1; }
exec 7>/opt/minicloud/locks/docker.deploy.lock
flock 7
exec 9>/opt/minicloud/locks/minicloud.deploy.lock
flock 9
COMPOSE=(docker compose -p minicloud-minicloud -f "${INFRA_CONFIG}" -f "${APP_CONFIG}")
PORT="$(jq -r '.services.backend.ports[0] | split(":")[1]' "${APP_CONFIG}")"
[[ "${PORT}" =~ ^[0-9]+$ ]] || exit 1
ENDPOINT="http://127.0.0.1:${PORT}/internal/registry/maintenance"
OPERATOR="$(sha256sum "${ROOT}/registry-auth/key.pem" | cut -d ' ' -f1)"
PRIVATE_DIR="$(mktemp -d)"
trap 'rm -rf "${PRIVATE_DIR}"' EXIT

rpc() {
  ACTION="$1" OWNER_TOKEN="${OWNER_TOKEN:-}" jq -cn '{action:env.ACTION, ownerToken:env.OWNER_TOKEN}' > "${PRIVATE_DIR}/request.json"
  printf 'url = "%s"\nrequest = "POST"\nheader = "X-Minicloud-Registry-Operator: %s"\nheader = "Content-Type: application/json"\ndata-binary = "@%s"\n' \
    "${ENDPOINT}" "${OPERATOR}" "${PRIVATE_DIR}/request.json" | curl --config - --fail --silent --show-error --max-time 300
}

# A failed earlier run can leave the registry stopped/read-only and writes fenced.
"${COMPOSE[@]}" up -d --no-deps --pull never registry >/dev/null
OWNER_TOKEN="$(rpc begin | jq -r '.ownerToken')"
[[ "${OWNER_TOKEN}" =~ ^[a-f0-9]{64}$ ]] || exit 1
sleep 6

BACKUPS="${ROOT}/registry-backups"
mkdir -p "${BACKUPS}"
chmod 700 "${BACKUPS}"
DB="$(jq -r '.services.db.environment.POSTGRES_DB' "${INFRA_CONFIG}")"
"${COMPOSE[@]}" exec -T db pg_dump --format=custom --no-owner --no-acl -U minicloud -d "${DB}" </dev/null > "${PRIVATE_DIR}/metadata.dump"
REQUIRED="$(du -sb "${ROOT}/registry" "${ROOT}/registry-auth" "${PRIVATE_DIR}/metadata.dump" | awk '{total += $1} END {print total}')"
AVAILABLE="$(df -B1 --output=avail "${ROOT}" | tail -1 | tr -d ' ')"
TOTAL="$(df -B1 --output=size "${ROOT}" | tail -1 | tr -d ' ')"
FLOOR=$((TOTAL * 15 / 100))
if [ "${FLOOR}" -lt 10737418240 ]; then FLOOR=10737418240; fi
if [ "$((AVAILABLE - REQUIRED))" -lt "${FLOOR}" ]; then
  echo 'Insufficient registry backup reserve; writes remain fenced. Increase storage and rerun maintenance.' >&2
  exit 1
fi
NAME="registry-$(date -u +%Y%m%dT%H%M%SZ).tar"
tar -cf "${BACKUPS}/${NAME}.partial" -C "${ROOT}" registry registry-auth -C "${PRIVATE_DIR}" metadata.dump
tar -tf "${BACKUPS}/${NAME}.partial" >/dev/null
mv "${BACKUPS}/${NAME}.partial" "${BACKUPS}/${NAME}"
# Retain two complete private backups. Never traverse source/OpenBao directories.
mapfile -t OLD_BACKUPS < <(find "${BACKUPS}" -maxdepth 1 -type f -name 'registry-*.tar' -printf '%f\n' | sort -r | tail -n +3)
for old in "${OLD_BACKUPS[@]}"; do rm -f "${BACKUPS}/${old}"; done

if [ "${MODE}" == gc ]; then
  rpc sweep >/dev/null
  printf '{"services":{"registry":{"environment":{"REGISTRY_STORAGE_MAINTENANCE_READONLY_ENABLED":"true"}}}}' > "${PRIVATE_DIR}/readonly.json"
  "${COMPOSE[@]}" -f "${PRIVATE_DIR}/readonly.json" up -d --no-deps --pull never --force-recreate registry >/dev/null
  CONTAINER="$("${COMPOSE[@]}" ps -q registry)"
  IMAGE="$(jq -r '.services.registry.image' "${APP_CONFIG}")"
  docker run --rm --network none --read-only --user 1000:1000 --cap-drop ALL --security-opt no-new-privileges \
    --memory 512m --cpus 1 --pids-limit 128 --volumes-from "${CONTAINER}" --entrypoint /bin/registry \
    "${IMAGE}" garbage-collect /etc/distribution/config.yml
  "${COMPOSE[@]}" up -d --no-deps --pull never --force-recreate registry >/dev/null
  ACTION=finish
else
  ACTION=unfence
fi
for attempt in $(seq 1 15); do
  if rpc "${ACTION}" >/dev/null; then
    echo "Registry ${MODE} complete; private backup ${NAME}."
    exit 0
  fi
  sleep 2
done
echo 'Registry did not recover; writes remain fenced. Rerun maintenance after inspection.' >&2
exit 1
